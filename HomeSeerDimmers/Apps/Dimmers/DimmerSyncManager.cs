using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.Client.Exceptions;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text.Json;
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
        /// Indicates whether a device needs another sync attempt so failed updates can be retried.
        /// </summary>
        private enum SyncLedDimmerResult
        {
            /// <summary>
            /// Synchronization succeeded, so no further attempts are needed.
            /// </summary>
            SyncSucceeded,

            /// <summary>
            /// A device update failed, so another synchronization pass should be attempted.
            /// </summary>
            Retry
        }

        /// <summary>
        /// Identifies HomeSeer devices in the registry so discovery excludes other manufacturers.
        /// </summary>
        private const string HomeseerManufacturer = "HomeSeer Technologies";

        /// <summary>
        /// Limits discovery to models whose LED configuration parameters are supported.
        /// </summary>
        private static readonly ImmutableHashSet<string> SupportedHomeseerDimmerModels = ImmutableHashSet.Create(
            StringComparer.InvariantCulture,
            new[]
            {
                "HS-WD200+", // => https://docs.homeseer.com/products/hs-wd200
                "HS-WX300" // => https://docs.homeseer.com/products/hs-wx300
            });

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
        /// Supplies discovery time so cache expiry can be controlled without waiting in tests.
        /// </summary>
        private readonly IScheduler scheduler;

        /// <summary>
        /// Discovered dimmer devices
        /// </summary>
        private ImmutableArray<HassDeviceExtended> dimmerDevices = [];

        /// <summary>
        /// Marks when cached discovery expires so network changes are picked up by a later sync.
        /// </summary>
        private DateTimeOffset nextDiscoveryTime = DateTimeOffset.MinValue;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger">Logger</param>
        /// <param name="appConfig">App configuration</param>
        /// <param name="runner">Home assistant connection runner</param>
        /// <param name="scheduler">Scheduler that supplies the discovery clock.</param>
        public DimmerSyncManager(
            ILogger<DimmerSyncManager> logger,
            IAppConfig<Config> appConfig,
            IHomeAssistantRunner runner,
            IScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(appConfig);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(scheduler);

            this.logger = logger;
            this.appConfig = appConfig;
            this.runner = runner;
            this.scheduler = scheduler;
        }

        /// <inheritdoc />
        public async Task SyncDimmersToAsync(LedInputTable input, CancellationToken cancellationToken)
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
                await this.DiscoverDimmersAsync(connection, cancellationToken);

                this.nextDiscoveryTime = this.scheduler.Now + this.appConfig.Value.ZWaveDevicesDiscoveryValidity;
            }

            SyncLedDimmerResult result = await this.SyncLedsOfDiscoveredDimmersAsync(connection, input, cancellationToken);
            if (result == SyncLedDimmerResult.Retry)
            {
                this.nextDiscoveryTime = DateTimeOffset.MinValue;
                // retry only once
                this.logger.LogInformation("Retrying");
                await this.SyncLedsOfDiscoveredDimmersAsync(connection, input, cancellationToken);
            }
        }

        /// <summary>
        /// Refreshes the supported, enabled dimmers so syncs use the current device registry.
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        private async Task DiscoverDimmersAsync(IHomeAssistantConnection connection, CancellationToken token)
        {
            this.logger.LogInformation("Discovering HomeSeer dimmers:");

            ImmutableArray<HassDeviceExtended> dimmerDevices =
                (await connection.GetDevicesExtendedAsync(token))
                    .Where(d => d.DisabledBy == null && d.TryGetZWaveNodeId(out _)
                        && string.Equals(HomeseerManufacturer, d.Manufacturer, StringComparison.InvariantCultureIgnoreCase)
                        && SupportedHomeseerDimmerModels.Contains(d.Model))
                    .ToImmutableArray();

            foreach (HassDeviceExtended device in dimmerDevices)
            {
                this.logger.LogInformation("  device: {Name}, {Manufacturer}, {Model}", device.Name, device.Manufacturer, device.Model);
            }

            this.dimmerDevices = dimmerDevices;
        }

        /// <summary>
        /// Syncs each discovered dimmer independently so one device failure does not block healthy devices.
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
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SyncLedDimmerResult dimmerSyncResult = await this.SyncLedsOfADimmerAsync(connection, device, input, cancellationToken);
                    if (dimmerSyncResult != SyncLedDimmerResult.SyncSucceeded)
                    {
                        result = dimmerSyncResult;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or HomeAssistantApiCallException or TimeoutException or JsonException)
                {
                    this.logger.LogError(ex, "Unable to sync Z-Wave LEDs for device: {Name}", device.Name);
                    result = SyncLedDimmerResult.Retry;
                }
            }
            timer.Stop();
            this.logger.LogInformation("Dimmer LED syncs completed in {Elapsed}: {Result}", timer.Elapsed, result);

            return result;
        }

        /// <summary>
        /// Compares one dimmer with the desired inputs and writes differences to avoid unnecessary Z-Wave traffic.
        /// </summary>
        /// <param name="connection">Home Assistant connection</param>
        /// <param name="device">Device to sync</param>
        /// <param name="input">LED input table</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the sync</returns>
        private async Task<SyncLedDimmerResult> SyncLedsOfADimmerAsync(IHomeAssistantConnection connection, HassDeviceExtended device, LedInputTable input, CancellationToken cancellationToken)
        {
            DimmerConfig config = await connection.GetZWaveDimmerConfigAsync(device, cancellationToken);

            SyncLedDimmerResult result = SyncLedDimmerResult.SyncSucceeded;

            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LedStatusColor newColor = input.Colors[i];
                LedBlink newBlink = input.Blinks[i];

                if (newColor == config.Colors[i])
                {
                    this.logger.LogTrace(
                        "No update needed for Z-Wave LED color: {Name}[{Index}]: {CurrentColor}",
                        device.Name,
                        i,
                        config.Colors[i]);
                }
                else
                {
                    this.logger.LogInformation(
                        "Updating Z-Wave LED color for: {Name}[{Index}]: {CurrentColor} => {NewColor}",
                        device.Name,
                        i,
                        config.Colors[i],
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

                if (newBlink == config.Blinks[i])
                {
                    this.logger.LogTrace(
                        "No update needed for Z-Wave LED blink: {Name}[{Index}]: {CurrentBlink}",
                        device.Name,
                        i,
                        config.Blinks[i] == LedBlink.On ? "*" : ".");
                }
                else
                {
                    this.logger.LogInformation(
                        "Updating Z-Wave LED blink for: {Name}[{Index}]: {CurrentBlink} => {NewBlink}",
                        device.Name,
                        i,
                        config.Blinks[i] == LedBlink.On ? "*" : ".",
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

            if (config.CustomLedStatusMode == CustomLedStatusMode.Enable)
            {
                this.logger.LogTrace(
                    "No update needed for Z-Wave EnableCustomLedStatusMode: {Name}: {Mode}",
                    device.Name,
                    config.CustomLedStatusMode);
            }
            else
            {
                this.logger.LogInformation(
                    "Updating Z-Wave EnableCustomLedStatusMode for: {Name}: {CurrentMode} => {NewMode}",
                    device.Name,
                    config.CustomLedStatusMode,
                    CustomLedStatusMode.Enable);

                try
                {
                    await connection.SetZWaveCustomLedStatusModeAsync(device, CustomLedStatusMode.Enable, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    this.logger.LogError(ex, "Unable to set Z-Wave EnableCustomLedStatusMode for device: {Name}", device.Name);

                    result = SyncLedDimmerResult.Retry;
                }
            }

            if (config.BlinkFrequency == this.appConfig.Value.DimmerLedBlinkFrequency)
            {
                this.logger.LogTrace(
                    "No update needed for Z-Wave LED blink frequency: {Name}: {CurrentBlinkFrequency}",
                    device.Name,
                    config.BlinkFrequency);
            }
            else
            {
                this.logger.LogInformation(
                    "Updating Z-Wave LED blink frequency for: {Name}: {CurrentBlinkFrequency} => {NewBlinkFrequency}",
                    device.Name,
                    config.BlinkFrequency,
                    this.appConfig.Value.DimmerLedBlinkFrequency);

                try
                {
                    await connection.SetZWaveBlinkFrequencyAsync(device, this.appConfig.Value.DimmerLedBlinkFrequency, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    this.logger.LogError(ex, "Unable to set Z-Wave LED blink frequency for device: {Name}", device.Name);

                    result = SyncLedDimmerResult.Retry;
                }
            }

            return result;
        }
    }
}