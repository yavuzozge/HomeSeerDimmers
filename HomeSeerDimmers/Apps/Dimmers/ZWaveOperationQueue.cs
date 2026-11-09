using Microsoft.Extensions.Logging;
using NetDaemon.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Runs device callbacks serially and combines duplicate pending requests.
    /// </summary>
    internal sealed class ZWaveOperationQueue : IAsyncDisposable
    {
        /// <summary>
        /// Records failed operations so connection and device problems remain visible.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// Protects pending requests and lifecycle changes so producers cannot race with shutdown.
        /// </summary>
        private readonly object stateLock = new();

        /// <summary>
        /// Holds bounded pending work so slow devices cannot cause an unlimited backlog.
        /// </summary>
        private readonly Channel<Func<CancellationToken, Task>> operations;

        /// <summary>
        /// Tracks callback identities so duplicate requests share one pending operation.
        /// </summary>
        private readonly HashSet<Func<CancellationToken, Task>> pending = [];

        /// <summary>
        /// Interrupts active device requests when the queue is stopped.
        /// </summary>
        private readonly CancellationTokenSource cancellation = new();

        /// <summary>
        /// Represents execution after startup so failed app construction does not start device traffic.
        /// </summary>
        private Task? worker;

        /// <summary>
        /// Shares cleanup across disposal callers so cancellation runs only once.
        /// </summary>
        private Task? disposalTask;

        /// <summary>
        /// Rejects new requests as soon as shutdown starts.
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Creates an inactive queue so its owner can finish registering callbacks before starting it.
        /// </summary>
        /// <param name="logger">Logger used for operation failures.</param>
        /// <param name="capacity">Maximum number of distinct pending callbacks.</param>
        internal ZWaveOperationQueue(ILogger logger, int capacity)
        {
            ArgumentNullException.ThrowIfNull(logger);
            this.logger = logger;
            this.operations = Channel.CreateBounded<Func<CancellationToken, Task>>(
                new BoundedChannelOptions(capacity) { SingleReader = true });
        }

        /// <summary>
        /// Starts the worker without invoking device callbacks under the queue lock.
        /// </summary>
        internal void Start()
        {
            lock (this.stateLock)
            {
                if (!this.disposed && this.worker == null)
                {
                    this.worker = Task.Run(this.ExecuteAsync);
                }
            }
        }

        /// <summary>
        /// Adds a callback unless it is already pending or shutdown has started.
        /// </summary>
        /// <param name="operation">Callback that receives the queue's cancellation token.</param>
        /// <exception cref="InvalidOperationException">The bounded queue is full or the worker has stopped.</exception>
        internal void Enqueue(Func<CancellationToken, Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            lock (this.stateLock)
            {
                if (this.disposed || !this.pending.Add(operation))
                {
                    return;
                }
                if (!this.operations.Writer.TryWrite(operation))
                {
                    _ = this.pending.Remove(operation);
                    throw new InvalidOperationException("The Z-Wave operation queue is unavailable");
                }
            }
        }

        /// <summary>
        /// Rejects pending work immediately and cancels requests outside the queue lock.
        /// </summary>
        /// <returns>A task that completes after active work and cancellation cleanup finish.</returns>
        public ValueTask DisposeAsync()
        {
            lock (this.stateLock)
            {
                if (this.disposalTask == null)
                {
                    this.disposed = true;
                    _ = this.operations.Writer.TryComplete();
                    this.disposalTask = Task.Run(this.StopAsync);
                }
                return new ValueTask(this.disposalTask);
            }
        }

        /// <summary>
        /// Cancels active work without holding a lock that device cancellation callbacks may need.
        /// </summary>
        /// <returns>A task that completes after the worker exits.</returns>
        private async Task StopAsync()
        {
            try
            {
                try
                {
                    this.cancellation.Cancel();
                }
                finally
                {
                    if (this.worker != null)
                    {
                        await this.worker.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                this.cancellation.Dispose();
            }
        }

        /// <summary>
        /// Runs callbacks one at a time and makes each active callback eligible for one follow-up request.
        /// </summary>
        /// <returns>A task that completes when the queue stops, or faults on an unexpected failure.</returns>
        private async Task ExecuteAsync()
        {
            CancellationToken token = this.cancellation.Token;
            try
            {
                await foreach (Func<CancellationToken, Task> operation in this.operations.Reader.ReadAllAsync(token))
                {
                    lock (this.stateLock)
                    {
                        if (this.disposed)
                        {
                            break;
                        }
                        _ = this.pending.Remove(operation);
                    }
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
                this.Complete();
                this.logger.LogError(ex, "The Z-Wave operation worker stopped unexpectedly");
                throw;
            }
            finally
            {
                this.Complete();
            }
        }

        /// <summary>
        /// Closes the channel and clears duplicate tracking together so stopped work cannot look already queued.
        /// </summary>
        private void Complete()
        {
            lock (this.stateLock)
            {
                _ = this.operations.Writer.TryComplete();
                this.pending.Clear();
            }
        }
    }
}