using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client.Exceptions;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Integration;
using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Coordinates LED synchronization and device refreshes through one cancellable work queue.
    /// </summary>
    [NetDaemonApp]
    public class App : IAsyncDisposable
    {
        /// <summary>
        /// Logger.
        /// </summary>
        private readonly ILogger<App> logger;

        /// <summary>
        /// Applies desired LED settings so the coordinator does not need to handle device parameters itself.
        /// </summary>
        private readonly IDimmerSyncManager dimmerDeviceManager;

        /// <summary>
        /// Refreshes device values so missed Home Assistant notifications can be recovered.
        /// </summary>
        private readonly IZWavePingManager zwavePingManager;

        /// <summary>
        /// Protects input, queue, and shutdown state so concurrent callbacks cannot lose updates.
        /// </summary>
        private readonly object stateLock = new();

        /// <summary>
        /// Owns input, timer, and service subscriptions so shutdown can release them together.
        /// </summary>
        private readonly CompositeDisposable subscriptions = new();

        /// <summary>
        /// Cancels the worker and device requests so stopping the app interrupts active work.
        /// </summary>
        private readonly CancellationTokenSource cancellation = new();

        /// <summary>
        /// Holds one pending synchronization and one pending ping; the pending flags prevent duplicates.
        /// </summary>
        private readonly Channel<Func<CancellationToken, Task>> operations =
            Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(2) { SingleReader = true });

        /// <summary>
        /// Represents the queue worker so shutdown can wait until active work has stopped.
        /// </summary>
        private readonly Task worker;

        /// <summary>
        /// Stores the latest desired LED state so queued and periodic syncs do not use stale inputs.
        /// </summary>
        private LedInputTable lastInputTable = new();

        /// <summary>
        /// Tracks a queued sync so repeated input changes share one pending operation.
        /// </summary>
        private bool ledSyncPending;

        /// <summary>
        /// Tracks a queued ping so timer ticks do not build a backlog of duplicate refreshes.
        /// </summary>
        private bool pingPending;

        /// <summary>
        /// Marks the start of shutdown so callbacks cannot queue new work while resources are released.
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Shares the shutdown task so repeated disposal requests wait for the same cleanup.
        /// </summary>
        private Task? disposalTask;

        /// <summary>
        /// Subscribes to LED inputs, registers the synchronization service, and starts the work queue.
        /// </summary>
        /// <param name="ha">Home Assistant context.</param>
        /// <param name="scheduler">Scheduler for periodic operations.</param>
        /// <param name="logger">Application logger.</param>
        /// <param name="ledInputMonitor">Source of desired LED states.</param>
        /// <param name="dimmerDeviceManager">LED synchronization manager.</param>
        /// <param name="zwavePingManager">Device refresh manager.</param>
        /// <param name="appConfig">Application configuration.</param>
        public App(
            IHaContext ha,
            IScheduler scheduler,
            ILogger<App> logger,
            ILedInputMonitor ledInputMonitor,
            IDimmerSyncManager dimmerDeviceManager,
            IZWavePingManager zwavePingManager,
            IAppConfig<Config> appConfig)
        {
            ArgumentNullException.ThrowIfNull(ha);
            ArgumentNullException.ThrowIfNull(scheduler);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(ledInputMonitor);
            ArgumentNullException.ThrowIfNull(dimmerDeviceManager);
            ArgumentNullException.ThrowIfNull(zwavePingManager);
            ArgumentNullException.ThrowIfNull(appConfig);

            this.logger = logger;
            this.dimmerDeviceManager = dimmerDeviceManager;
            this.zwavePingManager = zwavePingManager;

            try
            {
                this.subscriptions.Add(ledInputMonitor.AllInputTableChanges.Subscribe(this.ScheduleLedSync));

                if (appConfig.Value.LedSyncInterval > TimeSpan.Zero)
                {
                    logger.LogInformation("Scheduling periodic LED sync with interval: {Interval}", appConfig.Value.LedSyncInterval);
                    this.subscriptions.Add(scheduler.SchedulePeriodic(appConfig.Value.LedSyncInterval, this.ScheduleLedReSync));
                }

                if (appConfig.Value.ZWavePingInterval > TimeSpan.Zero)
                {
                    logger.LogInformation("Scheduling periodic ZWave pings with interval: {Interval}", appConfig.Value.ZWavePingInterval);
                    this.subscriptions.Add(scheduler.SchedulePeriodic(appConfig.Value.ZWavePingInterval, this.ScheduleZWavePing));
                }

                this.subscriptions.Add(ha.RegisterService<SynchronizeDimmersData>("synchronize_dimmers")
                    .Subscribe(_ => this.ScheduleLedReSync()));
                this.worker = this.ExecuteZWaveOperationsAsync();
            }
            catch
            {
                this.subscriptions.Dispose();
                this.operations.Writer.TryComplete();
                this.cancellation.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Cancels active work, discards queued work, and releases subscriptions.
        /// </summary>
        /// <returns>A task that completes when the worker has stopped.</returns>
        public ValueTask DisposeAsync()
        {
            lock (this.stateLock)
            {
                return new ValueTask(this.disposalTask ??= this.StopAsync());
            }
        }

        /// <summary>
        /// Closes the queue, cancels requests, and releases subscriptions so no work survives shutdown.
        /// </summary>
        /// <returns>A task that completes after the worker stops and its cancellation source is released.</returns>
        private async Task StopAsync()
        {
            this.disposed = true;
            this.operations.Writer.TryComplete();
            try
            {
                this.cancellation.Cancel();
                this.subscriptions.Dispose();
                await this.worker.ConfigureAwait(false);
            }
            finally
            {
                this.subscriptions.Dispose();
                this.cancellation.Dispose();
            }
        }

        /// <summary>
        /// Runs one operation at a time so Z-Wave request groups do not overlap, and logs failures for diagnosis.
        /// </summary>
        /// <returns>A task that completes when the queue closes or shutdown cancels the worker.</returns>
        private async Task ExecuteZWaveOperationsAsync()
        {
            CancellationToken token = this.cancellation.Token;
            try
            {
                await foreach (Func<CancellationToken, Task> operation in this.operations.Reader.ReadAllAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await operation(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when ((ex is OperationCanceledException && !token.IsCancellationRequested)
                        || ex is InvalidOperationException or HomeAssistantApiCallException
                            or HomeAssistantConnectionException or TimeoutException or IOException or JsonException)
                    {
                        this.logger.LogError(ex, "Unable to complete a queued Z-Wave operation");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "The Z-Wave operation worker stopped unexpectedly");
                throw;
            }
            finally
            {
                this.operations.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Adds device work to the shared queue so it uses the same serial execution and cancellation rules.
        /// </summary>
        /// <param name="createTask">Callback that starts an operation with the worker's cancellation token.</param>
        /// <exception cref="InvalidOperationException">The queue is full or closed and cannot accept the operation.</exception>
        private void ScheduleZwaveOperation(Func<CancellationToken, Task> createTask)
        {
            if (!this.operations.Writer.TryWrite(createTask))
            {
                throw new InvalidOperationException("The Z-Wave operation queue is unavailable");
            }
        }

        /// <summary>
        /// Saves the newest input and requests a sync so sensor changes reach the physical LEDs.
        /// </summary>
        /// <param name="inputTable">Latest LED colors and blink states reported by the input monitor.</param>
        private void ScheduleLedSync(LedInputTable inputTable)
        {
            lock (this.stateLock)
            {
                this.lastInputTable = inputTable;
                this.ScheduleLedReSync();
            }
        }

        /// <summary>
        /// Queues a sync only when one is not already pending so repeated requests are combined.
        /// </summary>
        private void ScheduleLedReSync()
        {
            lock (this.stateLock)
            {
                if (!this.disposed && !this.ledSyncPending)
                {
                    this.ledSyncPending = true;
                    this.ScheduleZwaveOperation(this.SyncLedsAsync);
                }
            }
        }

        /// <summary>
        /// Starts a sync using the latest inputs and allows another sync to be queued for changes received during it.
        /// </summary>
        /// <param name="token">Cancels device requests when the app stops.</param>
        /// <returns>The device synchronization task.</returns>
        private Task SyncLedsAsync(CancellationToken token)
        {
            LedInputTable input;
            lock (this.stateLock)
            {
                this.ledSyncPending = false;
                input = this.lastInputTable;
            }
            token.ThrowIfCancellationRequested();
            return this.dimmerDeviceManager.SyncDimmersToAsync(input, token);
        }

        /// <summary>
        /// Queues a refresh only when one is not already pending so timer ticks cannot flood the network.
        /// </summary>
        private void ScheduleZWavePing()
        {
            lock (this.stateLock)
            {
                if (!this.disposed && !this.pingPending)
                {
                    this.pingPending = true;
                    this.ScheduleZwaveOperation(this.PingDevicesAsync);
                }
            }
        }

        /// <summary>
        /// Starts the queued refresh and allows the next refresh to be scheduled while it runs.
        /// </summary>
        /// <param name="token">Cancels device requests when the app stops.</param>
        /// <returns>The device refresh task.</returns>
        private Task PingDevicesAsync(CancellationToken token)
        {
            lock (this.stateLock)
            {
                this.pingPending = false;
            }
            token.ThrowIfCancellationRequested();
            return this.zwavePingManager.PingDevicesAsync(token);
        }
    }
}