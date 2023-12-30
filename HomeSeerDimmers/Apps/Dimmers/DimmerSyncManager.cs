using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Syncs dimmers
    /// </summary>
    public class DimmerSyncManager : IDimmerSyncManager
    {
        /// <summary>
        /// Indicates the sync result
        /// </summary>
        private enum SyncLedDimmerResult
        {
            SyncSucceeded,
            Retry
        }

        /// <summary>
        /// logger
        /// </summary>
        private readonly ILogger<DimmerSyncManager> logger;

        /// <summary>
        /// App configuration
        /// </summary>
        private readonly IAppConfig<Config> appConfig;

        /// <summary>
        /// Home assistant connection runner
        /// </summary>
        private readonly IHomeAssistantRunner runner;

        /// <summary>
        /// Discovered dimmer devices
        /// </summary>
        private IEnumerable<HassDeviceExtended> dimmerDevices = Enumerable.Empty<HassDeviceExtended>();

        /// <summary>
        /// When the next discovery should be done (relative to system start time)
        /// </summary>
        private TimeSpan nextDiscoveryTime = TimeSpan.MinValue;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger">Logger</param>
        /// <param name="appConfig">App configuration</param>
        /// <param name="runner">Home assistant connection runner</param>
        public DimmerSyncManager(
            ILogger<DimmerSyncManager> logger,
            IAppConfig<Config> appConfig,
            IHomeAssistantRunner runner)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(appConfig);
            ArgumentNullException.ThrowIfNull(runner);

            this.logger = logger;
            this.appConfig = appConfig;
            this.runner = runner;
        }

        /// <inheritdoc />
        public async Task SyncDimmersToAsync(LedInputTable input, CancellationToken cancellationToken)
        {
            IHomeAssistantConnection? connection = this.runner.CurrentConnection;
            if (connection == null)
            {
                this.logger.LogError("The Home Assistant connection obtained was null, skipping sync");
                return;
            }

            TimeSpan sinceSystemStart = TimeSpan.FromTicks(Environment.TickCount);
            if (this.nextDiscoveryTime <= sinceSystemStart)
            {
                await this.DiscoverDimmersAsync(connection, cancellationToken);

                this.nextDiscoveryTime = sinceSystemStart + this.appConfig.Value.ZWaveDevicesDiscoveryValidity;
            }

            SyncLedDimmerResult result = await this.SyncLedsOfDiscoveredDimmersAsync(connection, input, cancellationToken);
            if (result == SyncLedDimmerResult.Retry)
            {
                // retry only once
                this.logger.LogInformation("Retrying");
                await this.SyncLedsOfDiscoveredDimmersAsync(connection, input, cancellationToken);
            }
        }

        /// <summary>
        /// Discovers the dimmers that will be used in sync operation
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        private async Task DiscoverDimmersAsync(IHomeAssistantConnection connection, CancellationToken token)
        {
            this.logger.LogInformation("Discovering HomeSeer dimmers:");

            IEnumerable<HassDeviceExtended> devices = (await connection.GetDevicesExtendedAsync(token)).ToArray();

            // HS-WD200+ => https://docs.homeseer.com/products/lighting/legacy-lighting/hs-wd200+
            // HS-WX300 (HS-WX300 is not tested) => https://docs.homeseer.com/products/lighting/hs-wx300
            IEnumerable<HassDeviceExtended> dimmerDevices = devices
                .Where(d => string.Equals("HomeSeer Technologies", d.Manufacturer, StringComparison.InvariantCultureIgnoreCase) &&
                    (string.Equals("HS-WD200+", d.Model, StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals("HS-WX300", d.Model, StringComparison.InvariantCultureIgnoreCase)))
                .ToArray();

            foreach (HassDeviceExtended device in dimmerDevices)
            {
                this.logger.LogInformation("  device: {Name}, {Manufacturer}, {Model}", device.Name, device.Manufacturer, device.Model);
            }

            this.dimmerDevices = dimmerDevices;
        }

        /// <summary>
        /// Syncs LEDs of all dimmers that were discovered
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="input">LED input table</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the sync</returns>
        private async Task<SyncLedDimmerResult> SyncLedsOfDiscoveredDimmersAsync(IHomeAssistantConnection connection, LedInputTable input, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            SyncLedDimmerResult result = SyncLedDimmerResult.SyncSucceeded;
            foreach (HassDeviceExtended device in this.dimmerDevices)
            {
                SyncLedDimmerResult dimmerSyncResult = await this.SyncLedsOfADimmerAsync(connection, device, input, cancellationToken);
                if (dimmerSyncResult != SyncLedDimmerResult.SyncSucceeded)
                {
                    result = dimmerSyncResult;
                }
            }
            timer.Stop();
            this.logger.LogInformation("Dimmer LED syncs completed in {Elapsed}: {Result}", timer.Elapsed, result);

            return result;
        }

        /// <summary>
        /// Syncs LEDs of a dimmer
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="device">Device to sync</param>
        /// <param name="input">LED input table</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the sync</returns>
        private async Task<SyncLedDimmerResult> SyncLedsOfADimmerAsync(IHomeAssistantConnection connection, HassDeviceExtended device, LedInputTable input, CancellationToken cancellationToken)
        {
            IReadOnlyList<(LedStatusColor color, LedBlink blink)>? deviceLedConfig = await connection.GetZWaveDimmerLedValuesAsync(device, cancellationToken);
            if (deviceLedConfig == null)
            {
                this.logger.LogWarning("Unable to read Z-Wave device configs, skiping: {Name}", device.Name);
                return SyncLedDimmerResult.SyncSucceeded;
            }

            SyncLedDimmerResult result = SyncLedDimmerResult.SyncSucceeded;

            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                LedStatusColor newColor = input.Colors[i];
                LedBlink newBlink = input.Blinks[i];

                if (newColor != deviceLedConfig[i].color)
                {
                    this.logger.LogInformation(
                        "Updating Z-Wave LED color for: {Name}[{Index}]: {CurrentColor} => {NewColor}",
                        device.Name,
                        i,
                        deviceLedConfig[i].color,
                        newColor);

                    try
                    {
                        await connection.SetZWaveLedColorAsync(device, i, newColor, cancellationToken);
                    }
                    catch (InvalidOperationException ex)
                    {
                        this.logger.LogError(ex, "Unable to set Z-Wave LED color for device: {Name}", device.Name);

                        result = SyncLedDimmerResult.Retry;
                    }
                }

                if (newBlink != deviceLedConfig[i].blink)
                {
                    this.logger.LogInformation(
                        "Updating Z-Wave LED blink for: {Name}[{Index}]: {CurrinetBlink} => {NewBlink}",
                        device.Name,
                        i,
                        deviceLedConfig[i].blink == LedBlink.On ? "*" : ".",
                        newBlink == LedBlink.On ? "*" : ".");

                    try
                    {
                        await connection.SetZWaveLedBlinkAsync(device, i, newBlink, cancellationToken);
                    }
                    catch (InvalidOperationException ex)
                    {
                        this.logger.LogError(ex, "Unable to set Z-Wave LED blink for device: {Name}", device.Name);

                        result = SyncLedDimmerResult.Retry;
                    }
                }
            }

            return result;
        }
    }
}
