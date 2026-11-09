using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.Client.Exceptions;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Pings Z-Wave devices
    /// </summary>
    public class ZWavePingManager : IZWavePingManager
    {
        /// <summary>
        /// Logger
        /// </summary>
        private readonly ILogger<ZWavePingManager> logger;

        /// <summary>
        /// Home Assistant runner
        /// </summary>
        private readonly IHomeAssistantRunner runner;

        /// <summary>
        /// App configuration
        /// </summary>
        private readonly IAppConfig<Config> appConfig;

        /// <summary>
        /// Supplies discovery time so cache expiry can be tested without waiting for real time to pass.
        /// </summary>
        private readonly IScheduler scheduler;

        /// <summary>
        /// Caches each selected device with its command class so refreshes use the requested values.
        /// </summary>
        private ImmutableArray<(HassDeviceExtended Device, ZWaveCommandClassId RefreshCommandClassId)> zwavePingDeviceMap = [];

        /// <summary>
        /// Marks when discovery expires so newly available or replaced devices can be found.
        /// </summary>
        private DateTimeOffset nextDiscoveryTime = DateTimeOffset.MinValue;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger">Logger</param>
        /// <param name="runner">Home Assistant runner</param>
        /// <param name="appConfig">App configuration</param>
        /// <param name="scheduler">Scheduler that supplies the discovery clock.</param>
        public ZWavePingManager(
            ILogger<ZWavePingManager> logger,
            IHomeAssistantRunner runner,
            IAppConfig<Config> appConfig,
            IScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(appConfig);
            ArgumentNullException.ThrowIfNull(scheduler);

            this.logger = logger;
            this.runner = runner;
            this.appConfig = appConfig;
            this.scheduler = scheduler;
        }

        /// <inheritdoc />
        public async Task PingDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IHomeAssistantConnection? connection = this.runner.CurrentConnection;
            if (connection == null)
            {
                this.logger.LogError("The Home Assistant connection obtained was null, skipping sync");
                return;
            }

            if (this.nextDiscoveryTime <= this.scheduler.Now)
            {
                await this.DiscoverDevicesAsync(connection, cancellationToken);
                this.nextDiscoveryTime = this.scheduler.Now + this.appConfig.Value.ZWaveDevicesDiscoveryValidity;
            }

            Stopwatch timer = Stopwatch.StartNew();
            foreach ((HassDeviceExtended device, ZWaveCommandClassId refreshCommandClassId) in this.zwavePingDeviceMap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await connection.RefreshNodeCcValuesAsync(device, refreshCommandClassId, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HomeAssistantApiCallException or TimeoutException)
                {
                    this.logger.LogError(ex, "Unable to refresh Z-Wave values for device: {Name}", device.Name);
                    this.nextDiscoveryTime = DateTimeOffset.MinValue;
                }
            }
            timer.Stop();

            this.logger.LogInformation("Device ping completed in {Elapsed}", timer.Elapsed);
        }

        /// <summary>
        /// Matches configured names to enabled Z-Wave devices and reports missing matches so refresh gaps are visible.
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        private async Task DiscoverDevicesAsync(IHomeAssistantConnection connection, CancellationToken token)
        {
            this.logger.LogInformation("Discovering Z-Wave devices to ping:");

            IEnumerable<HassDeviceExtended> devices = await connection.GetDevicesExtendedAsync(token);
            this.zwavePingDeviceMap = devices
                .Where(d => d.DisabledBy == null && d.TryGetZWaveNodeId(out _))
                .Select(d => (Device: d, this.appConfig.Value.ZWavePingDevices.FirstOrDefault(pd => string.Equals(pd.Name, d.Name, StringComparison.Ordinal))?.RefreshCommandClassId))
                .Where(ping => ping.RefreshCommandClassId != null)
                .Select(ping => (ping.Device, ping.RefreshCommandClassId ?? ZWaveCommandClassId.NoOperation))
                .ToImmutableArray();

            foreach ((HassDeviceExtended device, ZWaveCommandClassId refreshCommandClassId) in this.zwavePingDeviceMap)
            {
                this.logger.LogInformation("  {Name}: {class}, {Manufacturer}, {Model}", device.Name, refreshCommandClassId, device.Manufacturer, device.Model);
            }

            foreach (PingZwaveDevice configuredDevice in this.appConfig.Value.ZWavePingDevices)
            {
                if (!this.zwavePingDeviceMap.Any(ping => string.Equals(ping.Device.Name, configuredDevice.Name, StringComparison.Ordinal)))
                {
                    this.logger.LogWarning("Configured Z-Wave ping device was not found or is disabled: {Name}", configuredDevice.Name);
                }
            }
        }
    }
}