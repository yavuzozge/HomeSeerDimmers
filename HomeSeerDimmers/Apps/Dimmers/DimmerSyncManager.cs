using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.Client.Exceptions;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System;
using System.Collections.Generic;
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
    /// Synchronizes supported dimmers with desired LED settings and retries failed devices once.
    /// </summary>
    public class DimmerSyncManager : IDimmerSyncManager
    {
        /// <summary>
        /// Identifies HomeSeer devices so discovery excludes other manufacturers.
        /// </summary>
        private const string HomeseerManufacturer = "HomeSeer Technologies";

        /// <summary>
        /// Limits discovery to models whose LED parameter layout is supported.
        /// </summary>
        private static readonly ImmutableHashSet<string> SupportedHomeseerDimmerModels =
            ImmutableHashSet.Create(StringComparer.Ordinal, "HS-WD200+", "HS-WX300");

        /// <summary>
        /// Records device updates and failures so synchronization problems remain visible.
        /// </summary>
        private readonly ILogger<DimmerSyncManager> logger;

        /// <summary>
        /// Supplies desired blink timing and discovery cache duration.
        /// </summary>
        private readonly IAppConfig<Config> appConfig;

        /// <summary>
        /// Supplies the current connection rather than retaining a disconnected client.
        /// </summary>
        private readonly IHomeAssistantRunner runner;

        /// <summary>
        /// Supplies discovery time so cache expiry can be controlled in tests.
        /// </summary>
        private readonly IScheduler scheduler;

        /// <summary>
        /// Caches supported devices so every input change does not request the registry again.
        /// </summary>
        private ImmutableArray<HassDeviceExtended> dimmerDevices = [];

        /// <summary>
        /// Marks when discovery expires so changes to the network are picked up later.
        /// </summary>
        private DateTimeOffset nextDiscoveryTime = DateTimeOffset.MinValue;

        /// <summary>
        /// Creates a manager that uses the current connection and configured discovery timing.
        /// </summary>
        /// <param name="logger">Synchronization logger.</param>
        /// <param name="appConfig">Desired device settings and discovery timing.</param>
        /// <param name="runner">Home Assistant connection runner.</param>
        /// <param name="scheduler">Clock used for discovery expiry.</param>
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
        public Task SyncDimmersToAsync(LedInputTable input, CancellationToken cancellationToken)
        {
            return this.SyncDimmersAsync(input, false, cancellationToken);
        }

        /// <inheritdoc />
        public Task ReconcileDimmersToAsync(LedInputTable input, CancellationToken cancellationToken)
        {
            return this.SyncDimmersAsync(input, true, cancellationToken);
        }

        /// <summary>
        /// Shares device selection and bounded retries while keeping cached updates separate from physical reconciliation.
        /// </summary>
        /// <param name="input">Desired LED settings.</param>
        /// <param name="readDevice">Whether each attempt must read the physical configuration.</param>
        /// <param name="cancellationToken">Cancels registry, device reads, and writes.</param>
        /// <returns>A task that completes after device updates and their bounded retry.</returns>
        private async Task SyncDimmersAsync(LedInputTable input, bool readDevice, CancellationToken cancellationToken)
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

            ImmutableArray<HassDeviceExtended> failed = await this.SyncLedsOfDimmersAsync(
                connection, this.dimmerDevices, input, readDevice, cancellationToken);
            if (!failed.IsEmpty)
            {
                this.nextDiscoveryTime = DateTimeOffset.MinValue;
                this.logger.LogInformation("Retrying {Count} failed dimmers", failed.Length);
                _ = await this.SyncLedsOfDimmersAsync(connection, failed, input, readDevice, cancellationToken);
            }
        }

        /// <summary>
        /// Refreshes enabled supported devices so syncs follow the current registry.
        /// </summary>
        /// <param name="connection">Current Home Assistant connection.</param>
        /// <param name="token">Cancels registry requests.</param>
        /// <returns>A task that completes after the discovery cache is updated.</returns>
        private async Task DiscoverDimmersAsync(IHomeAssistantConnection connection, CancellationToken token)
        {
            this.logger.LogInformation("Discovering HomeSeer dimmers:");
            this.dimmerDevices = (await connection.GetDevicesExtendedAsync(token))
                .Where(d => d.DisabledBy == null && d.TryGetZWaveNodeId(out _)
                    && string.Equals(HomeseerManufacturer, d.Manufacturer, StringComparison.OrdinalIgnoreCase)
                    && SupportedHomeseerDimmerModels.Contains(d.Model))
                .ToImmutableArray();
            foreach (HassDeviceExtended device in this.dimmerDevices)
            {
                this.logger.LogInformation("  device: {Name}, {Manufacturer}, {Model}", device.Name, device.Manufacturer, device.Model);
            }
        }

        /// <summary>
        /// Processes devices independently and returns only failures so healthy devices are not retried.
        /// </summary>
        /// <param name="connection">Current Home Assistant connection.</param>
        /// <param name="devices">Devices selected for this attempt.</param>
        /// <param name="input">Desired LED settings.</param>
        /// <param name="readDevice">Whether configuration is read from the physical device.</param>
        /// <param name="token">Cancels device requests.</param>
        /// <returns>Devices that need another attempt.</returns>
        private async Task<ImmutableArray<HassDeviceExtended>> SyncLedsOfDimmersAsync(
            IHomeAssistantConnection connection, ImmutableArray<HassDeviceExtended> devices, LedInputTable input, bool readDevice, CancellationToken token)
        {
            Stopwatch timer = Stopwatch.StartNew();
            ImmutableArray<HassDeviceExtended>.Builder? failed = null;
            foreach (HassDeviceExtended device in devices)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!await this.SyncLedsOfADimmerAsync(connection, device, input, readDevice, token))
                    {
                        (failed ??= ImmutableArray.CreateBuilder<HassDeviceExtended>()).Add(device);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or HomeAssistantApiCallException or TimeoutException or JsonException)
                {
                    this.logger.LogError(ex, "Unable to sync Z-Wave LEDs for device: {Name}", device.Name);
                    (failed ??= ImmutableArray.CreateBuilder<HassDeviceExtended>()).Add(device);
                }
            }
            this.logger.LogInformation("Dimmer LED syncs completed in {Elapsed}; {FailedCount} failed", timer.Elapsed, failed?.Count ?? 0);
            return failed?.ToImmutable() ?? ImmutableArray<HassDeviceExtended>.Empty;
        }

        /// <summary>
        /// Compares one device with its desired settings while leaving parameter encoding to the device helpers.
        /// </summary>
        /// <param name="connection">Current Home Assistant connection.</param>
        /// <param name="device">Device being synchronized.</param>
        /// <param name="input">Desired LED settings.</param>
        /// <param name="readDevice">Whether configuration is read from the physical device.</param>
        /// <param name="token">Cancels device requests.</param>
        /// <returns>Whether every required setting was updated successfully.</returns>
        private async Task<bool> SyncLedsOfADimmerAsync(
            IHomeAssistantConnection connection, HassDeviceExtended device, LedInputTable input, bool readDevice, CancellationToken token)
        {
            DimmerConfig config = readDevice
                ? await connection.ReadZWaveDimmerConfigAsync(device, token)
                : await connection.GetZWaveDimmerConfigAsync(device, token);
            bool succeeded = true;
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                succeeded &= await this.SyncSettingAsync(device, $"LED {i} color", config.Colors[i], input.Colors[i],
                    () => connection.SetZWaveLedColorAsync(device, i, input.Colors[i], token), token);
                succeeded &= await this.SyncSettingAsync(device, $"LED {i} blink", config.Blinks[i], input.Blinks[i],
                    () => connection.SetZWaveLedBlinkAsync(device, i, input.Blinks[i], token), token);
            }
            succeeded &= await this.SyncSettingAsync(device, "custom LED status mode", config.CustomLedStatusMode, CustomLedStatusMode.Enable,
                () => connection.SetZWaveCustomLedStatusModeAsync(device, CustomLedStatusMode.Enable, token), token);
            int blinkFrequency = this.appConfig.Value.DimmerLedBlinkFrequency;
            succeeded &= await this.SyncSettingAsync(device, "LED blink frequency", config.BlinkFrequency, blinkFrequency,
                () => connection.SetZWaveBlinkFrequencyAsync(device, blinkFrequency, token), token);
            return succeeded;
        }

        /// <summary>
        /// Applies one changed setting with shared comparison, logging, and failure handling.
        /// </summary>
        /// <typeparam name="T">Device setting type.</typeparam>
        /// <param name="device">Device being updated.</param>
        /// <param name="setting">Setting name used in diagnostic messages.</param>
        /// <param name="current">Reported device value.</param>
        /// <param name="desired">Requested value.</param>
        /// <param name="write">Device helper that writes the requested value.</param>
        /// <param name="token">Cancels the operation before a write is started.</param>
        /// <returns>Whether the setting already matched or its write succeeded.</returns>
        private async Task<bool> SyncSettingAsync<T>(
            HassDeviceExtended device, string setting, T current, T desired, Func<Task> write, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (EqualityComparer<T>.Default.Equals(current, desired))
            {
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace("No update needed for Z-Wave {Setting}: {Name}: {Current}", setting, device.Name, current);
                }
                return true;
            }
            this.logger.LogInformation("Updating Z-Wave {Setting}: {Name}: {Current} => {Desired}", setting, device.Name, current, desired);
            try
            {
                await write();
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HomeAssistantApiCallException or TimeoutException or JsonException)
            {
                this.logger.LogError(ex, "Unable to set Z-Wave {Setting} for device: {Name}", setting, device.Name);
                return false;
            }
        }
    }
}