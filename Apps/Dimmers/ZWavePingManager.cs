using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Pings Z-Wave devices
    /// </summary>
    public class ZWavePingManager : IZWavePingManager
    {
        private readonly ILogger<ZWavePingManager> logger;
        private readonly IAppConfig<Config> appConfig;
        private readonly IHomeAssistantRunner runner;

        private IEnumerable<(HaDevice Device, ZWaveCommandClassId RefreshCommandClassId)> zwavePingDeviceMap = Enumerable.Empty<(HaDevice, ZWaveCommandClassId)>();
        private bool deviceDiscoveryCompleted = false;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="runner"></param>
        /// <param name="appConfig"></param>
        public ZWavePingManager(
            ILogger<ZWavePingManager> logger,
            IHomeAssistantRunner runner,
            IAppConfig<Config> appConfig)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(appConfig);

            this.logger = logger;
            this.runner = runner;
            this.appConfig = appConfig;
        }

        /// <inheritdoc />
        public async Task PingDevicesAsync(CancellationToken cancellationToken)
        {
            IHomeAssistantConnection? connection = this.runner.CurrentConnection;
            if (connection == null)
            {
                this.logger.LogError("The Home Assistant connection obtained was null, skipping sync");
                return;
            }

            if (!this.deviceDiscoveryCompleted)
            {
                await this.DiscoverDevicesAsync(connection, cancellationToken);
            }

            Stopwatch timer = Stopwatch.StartNew();
            foreach ((HaDevice device, ZWaveCommandClassId refreshCommandClassId) in this.zwavePingDeviceMap)
            {
                await connection.RefreshNodeCcValuesAsync(device, refreshCommandClassId, cancellationToken);
            }
            timer.Stop();

            this.logger.LogInformation("Device ping completed in {Elapsed}", timer.Elapsed);
        }

        /// <summary>
        /// Discovers the Z-Wave that will be used in ping operation
        /// The devices are defined in <see cref="Config"/>
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        private async Task DiscoverDevicesAsync(IHomeAssistantConnection connection, CancellationToken token)
        {
            this.logger.LogInformation("Discovering Z-Wave devices to ping:");

            IEnumerable<HaDevice> devices = await connection.GetDevicesAsync(token);
            this.zwavePingDeviceMap = devices
                .Select(d => (Device: d, this.appConfig.Value.ZWavePingDevices.FirstOrDefault(pd => string.Equals(pd.Name, d.Name, StringComparison.Ordinal))?.RefreshCommandClassId))
                .Where(ping => ping.RefreshCommandClassId != null)
                .Select(ping => (ping.Device, ping.RefreshCommandClassId ?? ZWaveCommandClassId.NoOperation))
                .ToArray();

            foreach ((HaDevice device, ZWaveCommandClassId refreshCommandClassId) in this.zwavePingDeviceMap)
            {
                this.logger.LogInformation("  {Name}: {class}, {Manufacturer}, {Model}", device.Name, refreshCommandClassId, device.Manufacturer, device.Model);
            }

            this.deviceDiscoveryCompleted = true;
        }
    }
}
