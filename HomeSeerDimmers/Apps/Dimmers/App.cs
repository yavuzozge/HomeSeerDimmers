using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Integration;
using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Connects LED inputs, periodic schedules, and Home Assistant services to device operations.
    /// </summary>
    [NetDaemonApp]
    public class App : IAsyncDisposable
    {
        /// <summary>
        /// Applies desired LED settings without exposing device parameters to the coordinator.
        /// </summary>
        private readonly IDimmerSyncManager dimmerDeviceManager;

        /// <summary>
        /// Protects the latest input and shutdown state so callbacks cannot lose updates.
        /// </summary>
        private readonly object stateLock = new();

        /// <summary>
        /// Owns input, timer, and service subscriptions so shutdown releases them together.
        /// </summary>
        private readonly CompositeDisposable subscriptions = new();

        /// <summary>
        /// Owns serial execution and cancellation so the app only coordinates operation requests.
        /// </summary>
        private readonly ZWaveOperationQueue operations;

        /// <summary>
        /// Reuses one callback so repeated sync requests can be combined without allocating delegates.
        /// </summary>
        private readonly Func<CancellationToken, Task> syncLeds;

        /// <summary>
        /// Reuses one callback so repeated timer ticks share one pending refresh.
        /// </summary>
        private readonly Func<CancellationToken, Task> pingDevices;

        /// <summary>
        /// Stores current desired settings so a queued sync never uses an obsolete captured table.
        /// </summary>
        private LedInputTable lastInputTable = new();

        /// <summary>
        /// Preserves full-resync requests when input changes share the same pending callback.
        /// </summary>
        private bool reconciliationRequested;

        /// <summary>
        /// Rejects callbacks once shutdown starts so subscriptions cannot request more work.
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Shares cleanup across disposal callers so all of them wait for the same shutdown.
        /// </summary>
        private Task? disposalTask;

        /// <summary>
        /// Registers input, timer, and service callbacks before starting device work.
        /// </summary>
        /// <param name="ha">Home Assistant context.</param>
        /// <param name="scheduler">Scheduler for periodic operations.</param>
        /// <param name="logger">Application logger.</param>
        /// <param name="ledInputMonitor">Source of desired LED settings.</param>
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

            this.dimmerDeviceManager = dimmerDeviceManager;
            this.syncLeds = this.SyncLedsAsync;
            this.pingDevices = zwavePingManager.PingDevicesAsync;
            this.operations = new ZWaveOperationQueue(logger, 2);
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
                this.operations.Start();
            }
            catch
            {
                this.operations.DisposeAsync().AsTask().GetAwaiter().GetResult();
                this.subscriptions.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Stops device work and releases callbacks without running external cleanup under the state lock.
        /// </summary>
        /// <returns>A task that completes after subscriptions and active operations have stopped.</returns>
        public ValueTask DisposeAsync()
        {
            lock (this.stateLock)
            {
                if (this.disposalTask == null)
                {
                    this.disposed = true;
                    Task queueShutdown = this.operations.DisposeAsync().AsTask();
                    this.disposalTask = Task.Run(() => this.StopAsync(queueShutdown));
                }
                return new ValueTask(this.disposalTask);
            }
        }

        /// <summary>
        /// Releases subscriptions outside the state lock so disposal callbacks cannot deadlock scheduling.
        /// </summary>
        /// <param name="queueShutdown">Shutdown already started by the operation queue.</param>
        /// <returns>A task that completes after both callback and worker cleanup.</returns>
        private async Task StopAsync(Task queueShutdown)
        {
            try
            {
                this.subscriptions.Dispose();
            }
            finally
            {
                await queueShutdown.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Saves the newest input before requesting a sync so queued work sees current sensor values.
        /// </summary>
        /// <param name="inputTable">Latest LED colors and blink states.</param>
        private void ScheduleLedSync(LedInputTable inputTable)
        {
            lock (this.stateLock)
            {
                if (this.disposed)
                {
                    return;
                }
                this.lastInputTable = inputTable;
            }
            this.operations.Enqueue(this.syncLeds);
        }

        /// <summary>
        /// Requests fresh device reads so periodic and manual resyncs can repair a stale Home Assistant cache.
        /// </summary>
        private void ScheduleLedReSync()
        {
            lock (this.stateLock)
            {
                if (this.disposed)
                {
                    return;
                }
                this.reconciliationRequested = true;
            }
            this.operations.Enqueue(this.syncLeds);
        }

        /// <summary>
        /// Reads the latest input at execution time so queued requests do not replay stale snapshots.
        /// </summary>
        /// <param name="token">Cancels device requests when the queue stops.</param>
        /// <returns>The device synchronization task.</returns>
        private Task SyncLedsAsync(CancellationToken token)
        {
            LedInputTable input;
            bool reconcile;
            lock (this.stateLock)
            {
                if (this.disposed)
                {
                    return Task.CompletedTask;
                }
                input = this.lastInputTable;
                reconcile = this.reconciliationRequested;
                this.reconciliationRequested = false;
            }
            token.ThrowIfCancellationRequested();
            return reconcile
                ? this.dimmerDeviceManager.ReconcileDimmersToAsync(input, token)
                : this.dimmerDeviceManager.SyncDimmersToAsync(input, token);
        }

        /// <summary>
        /// Requests a refresh through the queue so timer ticks cannot accumulate duplicate work.
        /// </summary>
        private void ScheduleZWavePing()
        {
            this.operations.Enqueue(this.pingDevices);
        }
    }
}