using Microsoft.Extensions.Logging;
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

        private readonly ILogger<DimmerSyncManager> logger;
        private readonly IHomeAssistantRunner runner;

        private IEnumerable<HaDevice> dimmerDevices = Enumerable.Empty<HaDevice>();
        private bool deviceDiscoveryCompleted = false;

        public DimmerSyncManager(
            ILogger<DimmerSyncManager> logger,
            IHomeAssistantRunner runner)
        {
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(runner, nameof(runner));

            this.logger = logger;
            this.runner = runner;
        }

        /// <inheritdoc />
        public async Task SyncDimmersAsync(LedInputTable input, CancellationToken cancellationToken)
        {
            IHomeAssistantConnection? connection = this.runner.CurrentConnection;
            if (connection == null)
            {
                this.logger.LogError("The Home Assistant connection obtained was null, skipping sync");
                return;
            }

            if (!this.deviceDiscoveryCompleted)
            {
                await this.DiscoverDimmersAsync(connection, cancellationToken);
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

            IEnumerable<HaDevice> devices = (await connection.GetDevicesAsync(token)).ToArray();

            // HS-WD200+ => https://docs.homeseer.com/products/lighting/legacy-lighting/hs-wd200+
            // HS-WX300 (HS-WX300 is not tested) => https://docs.homeseer.com/products/lighting/hs-wx300
            IEnumerable<HaDevice> dimmerDevices = devices
                .Where(d => string.Equals("HomeSeer Technologies", d.Manufacturer, StringComparison.InvariantCultureIgnoreCase) &&
                    (string.Equals("HS-WD200+", d.Model, StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals("HS-WX300", d.Model, StringComparison.InvariantCultureIgnoreCase)))
                .ToArray();

            foreach (HaDevice device in dimmerDevices)
            {
                this.logger.LogInformation("  device: {Name}, {Manufacturer}, {Model}", device.Name, device.Manufacturer, device.Model);
            }

            this.dimmerDevices = dimmerDevices;
            this.deviceDiscoveryCompleted = true;
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
            foreach (HaDevice device in this.dimmerDevices)
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
        private async Task<SyncLedDimmerResult> SyncLedsOfADimmerAsync(IHomeAssistantConnection connection, HaDevice device, LedInputTable input, CancellationToken cancellationToken)
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
