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
            return $"{zwaveNodeId}-112-0-2{index + 1}";
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
        /// Gets the current configuration for a HomeSeer dimmer ZWave device
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

            LedStatusColor[] colors = new LedStatusColor[7];
            LedBlink[] blinks = new LedBlink[7];

            ZwaveConfigParameters? config;
            for (int i = 0; i < 7; ++i)
            {
                if (!result.TryGetValue(CreateLedStatusColorConfigName(zwaveNodeId, i), out config) || !config.TryGetEnumeratedValue(out LedStatusColor color))
                {
                    throw new InvalidOperationException($"Failed to parse LED color: {i} for device {device.Name}");
                }
                colors[i] = color;

                if (!result.TryGetValue(CreateLedStatusBlinkConfigName(zwaveNodeId, i), out config) || !config.TryGetEnumeratedValue(out LedBlink blink))
                {
                    throw new InvalidOperationException($"Failed to parse LED blink: {i} for device {device.Name}");
                }
                blinks[i] = blink;
            }

            if (!result.TryGetValue(CreateCustomLedStatusModelConfigName(zwaveNodeId), out config) || !config.TryGetEnumeratedValue(out CustomLedStatusMode customLedStatusMode))
            {
                throw new InvalidOperationException($"Failed to parse EnableCustomLedStatus for device {device.Name}");
            }

            if (!result.TryGetValue(CreateBlinkFrequencyConfigName(zwaveNodeId), out config) || !config.TryGetIntValue(out int blinkFrequency))
            {
                throw new InvalidOperationException($"Failed to parse BlinkFrequencyConfigName for device {device.Name}");
            }

            return new DimmerConfig(colors.ToImmutableArray(), blinks.ToImmutableArray(), customLedStatusMode, blinkFrequency);
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
        public static async Task SetZWaveLedColorAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int index, LedStatusColor color, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, 21 + index, (int)color, cancellationToken);
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
        public static async Task SetZWaveLedBlinkAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int index, LedBlink blink, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, 31, 1 << index, (int)blink, cancellationToken);
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
        public static async Task SetZWaveCustomLedStatusModeAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CustomLedStatusMode mode, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, 13, (int)mode, cancellationToken);
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
        public static async Task SetZWaveBlinkFrequencyAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int blinkFrequency, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, 30, blinkFrequency, cancellationToken);
        }
    }
}