using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice
{
    /// <summary>
    /// Extension methods for <see cref="IHomeAssistantConnection"/> for Homeseer dimmer devices
    /// </summary>
    public static class DimmerLedZWaveExtensions
    {
        /// <summary>
        /// Creates a ZWave config name for a HomeSeer dimmer LED color
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <param name="index">LED index</param>
        /// <returns>ZWave config name</returns>
        private static string CreateLedStatusColorConfigName(int zwaveNodeId, int index)
        {
            return $"{zwaveNodeId}-112-0-{21 + index}";
        }

        /// <summary>
        /// Builds a blink parameter key using the LED's bit mask so its individual blink value can be read.
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <param name="index">LED index</param>
        /// <returns>ZWave config name</returns>
        private static string CreateLedStatusBlinkConfigName(int zwaveNodeId, int index)
        {
            return $"{zwaveNodeId}-112-0-31-{1 << index}";
        }

        /// <summary>
        /// Builds the status-mode key so synchronization can check whether custom LED colors are enabled.
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <returns>ZWave config name</returns>
        private static string CreateCustomLedStatusModelConfigName(int zwaveNodeId)
        {
            return $"{zwaveNodeId}-112-0-13";
        }

        /// <summary>
        /// Builds the blink-interval key so synchronization can compare the device's timing with the configured timing.
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <returns>ZWave config name</returns>
        private static string CreateBlinkFrequencyConfigName(int zwaveNodeId)
        {
            return $"{zwaveNodeId}-112-0-30";
        }

        /// <summary>
        /// Reads Home Assistant's cached dimmer configuration for the fast input-update path.
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Configruation</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        public static async Task<DimmerConfig> GetZWaveDimmerConfigAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            IDictionary<string, ZwaveConfigParameters> result = await connection.GetZWaveConfigParametersAsync(device, cancellationToken);

            int zwaveNodeId = device.GetZWaveNodeId();

            ImmutableArray<LedStatusColor>.Builder colors = ImmutableArray.CreateBuilder<LedStatusColor>(LedInputTable.NumberOfLeds);
            ImmutableArray<LedBlink>.Builder blinks = ImmutableArray.CreateBuilder<LedBlink>(LedInputTable.NumberOfLeds);

            ZwaveConfigParameters? config;
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                if (!result.TryGetValue(CreateLedStatusColorConfigName(zwaveNodeId, i), out config) || !config.TryGetEnumeratedValue(out LedStatusColor color))
                {
                    throw new InvalidOperationException($"Failed to parse LED color: {i} for device {device.Name}");
                }
                colors.Add(color);

                if (!result.TryGetValue(CreateLedStatusBlinkConfigName(zwaveNodeId, i), out config) || !config.TryGetEnumeratedValue(out LedBlink blink))
                {
                    throw new InvalidOperationException($"Failed to parse LED blink: {i} for device {device.Name}");
                }
                blinks.Add(blink);
            }

            if (!result.TryGetValue(CreateCustomLedStatusModelConfigName(zwaveNodeId), out config) || !config.TryGetEnumeratedValue(out CustomLedStatusMode customLedStatusMode))
            {
                throw new InvalidOperationException($"Failed to parse EnableCustomLedStatus for device {device.Name}");
            }

            if (!result.TryGetValue(CreateBlinkFrequencyConfigName(zwaveNodeId), out config)
                || !config.TryGetIntValue(out int blinkFrequency)
                || blinkFrequency is < 0 or > byte.MaxValue)
            {
                throw new InvalidOperationException($"Failed to parse BlinkFrequencyConfigName for device {device.Name}");
            }

            return new DimmerConfig(colors.MoveToImmutable(), blinks.MoveToImmutable(), customLedStatusMode, blinkFrequency);
        }

        /// <summary>
        /// Reads physical configuration values, waiting for every response instead of relying on Home Assistant's cache.
        /// </summary>
        /// <param name="connection">Current Home Assistant connection.</param>
        /// <param name="device">Dimmer whose physical settings are read.</param>
        /// <param name="cancellationToken">Cancels device reads.</param>
        /// <returns>A configuration decoded from fresh device responses.</returns>
        public static async Task<DimmerConfig> ReadZWaveDimmerConfigAsync(
            this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            ImmutableArray<LedStatusColor>.Builder colors = ImmutableArray.CreateBuilder<LedStatusColor>(LedInputTable.NumberOfLeds);
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                int value = await connection.GetRawZWaveConfigParameterAsync(device, 21 + i, cancellationToken);
                LedStatusColor color = (LedStatusColor)value;
                if (!Enum.IsDefined(color))
                {
                    throw new InvalidOperationException($"Invalid physical LED color for {device.Name}, LED {i}: {value}");
                }
                colors.Add(color);
            }

            int blinkMask = await connection.GetRawZWaveConfigParameterAsync(device, 31, cancellationToken);
            if (blinkMask < 0 || blinkMask >= (1 << LedInputTable.NumberOfLeds))
            {
                throw new InvalidOperationException($"Invalid physical LED blink mask for {device.Name}: {blinkMask}");
            }
            ImmutableArray<LedBlink>.Builder blinks = ImmutableArray.CreateBuilder<LedBlink>(LedInputTable.NumberOfLeds);
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                blinks.Add((blinkMask & (1 << i)) != 0 ? LedBlink.On : LedBlink.Off);
            }

            int modeValue = await connection.GetRawZWaveConfigParameterAsync(device, 13, cancellationToken);
            CustomLedStatusMode mode = (CustomLedStatusMode)modeValue;
            if (!Enum.IsDefined(mode))
            {
                throw new InvalidOperationException($"Invalid physical LED status mode for {device.Name}: {modeValue}");
            }
            int blinkFrequency = await connection.GetRawZWaveConfigParameterAsync(device, 30, cancellationToken);
            if (blinkFrequency is < 0 or > byte.MaxValue)
            {
                throw new InvalidOperationException($"Invalid physical blink interval for {device.Name}: {blinkFrequency}");
            }
            return new DimmerConfig(colors.MoveToImmutable(), blinks.MoveToImmutable(), mode, blinkFrequency);
        }

        /// <summary>
        /// Sets the current color of a LED of a HomeSeer dimmer ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="index">LED index (this is zero based index so 6 is top, 0 is bottom)</param>
        /// <param name="color">Color of the LED</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the index or color is invalid</exception>
        public static Task SetZWaveLedColorAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int index, LedStatusColor color, CancellationToken cancellationToken)
        {
            ValidateLedIndex(index);
            if (!Enum.IsDefined(color))
            {
                throw new ArgumentOutOfRangeException(nameof(color), color, "The LED color must be defined.");
            }
            return connection.SetZWaveConfigParameterAsync(device, 21 + index, (int)color, cancellationToken);
        }

        /// <summary>
        /// Sets the current blink of a LED of a HomeSeer dimmer ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="index">Zero-based LED index, from 0 at the bottom to 6 at the top.</param>
        /// <param name="blink">Whether the LED should blink</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the index or blink state is invalid</exception>
        public static Task SetZWaveLedBlinkAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int index, LedBlink blink, CancellationToken cancellationToken)
        {
            ValidateLedIndex(index);
            if (!Enum.IsDefined(blink))
            {
                throw new ArgumentOutOfRangeException(nameof(blink), blink, "The LED blink state must be defined.");
            }
            return connection.SetZWaveConfigParameterAsync(device, 31, 1 << index, (int)blink, cancellationToken);
        }

        /// <summary>
        /// Sets the current enable custom LED status mode of a HomeSeer dimmer ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="mode">Enable custom led status mode</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the mode is undefined</exception>
        public static Task SetZWaveCustomLedStatusModeAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CustomLedStatusMode mode, CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "The custom LED status mode must be defined.");
            }
            return connection.SetZWaveConfigParameterAsync(device, 13, (int)mode, cancellationToken);
        }

        /// <summary>
        /// Sets the blink interval of a HomeSeer dimmer in units of 100 milliseconds.
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="blinkFrequency">Blink frequency</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the frequency is outside 0–255</exception>
        public static Task SetZWaveBlinkFrequencyAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int blinkFrequency, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(blinkFrequency);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(blinkFrequency, byte.MaxValue);
            return connection.SetZWaveConfigParameterAsync(device, 30, blinkFrequency, cancellationToken);
        }

        /// <summary>
        /// Rejects LED positions outside the dimmer's LED array.
        /// </summary>
        /// <param name="index">Zero-based LED position.</param>
        private static void ValidateLedIndex(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, LedInputTable.NumberOfLeds);
        }
    }
}