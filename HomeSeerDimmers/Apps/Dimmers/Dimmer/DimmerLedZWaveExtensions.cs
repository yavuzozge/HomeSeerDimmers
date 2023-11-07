using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Dimmer
{
    /// <summary>
    /// Extension methods for <see cref="IHomeAssistantConnection"/> for Z-Wave devices
    /// </summary>
    public static class DimmerLedZWaveExtensions
    {
        /// <summary>
        /// Helper method that creates a ZWave config name for a HomeSeer dimmer LED color
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <param name="index">LED index</param>
        /// <returns>ZWave config name</returns>
        private static string CreateLedStatusColorConfigName(int zwaveNodeId, int index)
        {
            return $"{zwaveNodeId}-112-0-2{index + 1}";
        }

        /// <summary>
        /// Helper method that creates a ZWave config name for a HomeSeer dimmer LED blink
        /// </summary>
        /// <param name="zwaveNodeId">ZWave node ID</param>
        /// <param name="index">LED index</param>
        /// <returns>ZWave config name</returns>
        private static string CreateLedStatusBlinkConfigName(int zwaveNodeId, int index)
        {
            return $"{zwaveNodeId}-112-0-31-{1 << index}";
        }

        /// <summary>
        /// Gets the current values of LEDs of a HomeSeer dimmer ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Collection of LED values</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        public static async Task<IReadOnlyList<(LedStatusColor color, LedBlink blink)>> GetZWaveDimmerLedValuesAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            IDictionary<string, ZwaveConfigParameters> result = await connection.GetZWaveConfigParametersAsync(device, cancellationToken);

            int zwaveNodeId = device.GetZWaveNodeId();

            (LedStatusColor color, LedBlink blink)[] configs = new (LedStatusColor color, LedBlink blink)[7];
            for (int i = 0; i < configs.Length; ++i)
            {
                if (!result.TryGetValue(CreateLedStatusColorConfigName(zwaveNodeId, i), out ZwaveConfigParameters? config) || config.TryGetEnumeratedValue(out LedStatusColor color))
                {
                    throw new InvalidOperationException($"Failed to parse LED color: {i} for device {device.Name}");
                }

                if (!result.TryGetValue(CreateLedStatusBlinkConfigName(zwaveNodeId, i), out config) || config.TryGetEnumeratedValue(out LedBlink blink))
                {
                    throw new InvalidOperationException($"Failed to parse LED blink: {i} for device {device.Name}");
                }

                configs[i] = new(color, blink);
            }

            return configs;
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
            await connection.SetZWaveConfigParameterAsync(device, 21 + index, ((int)color).ToString(), cancellationToken);
        }

        /// <summary>
        /// Sets the current blink of a LED of a HomeSeer dimmer ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">HomeSeer dimmer device</param>
        /// <param name="index">LED index (7 top, 0 bottom)</param>
        /// <param name="blink">Whether the LED should blink</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        public static async Task SetZWaveLedBlinkAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int index, LedBlink blink, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, 31, 1 << index, blink == LedBlink.On ? "1" : "0", cancellationToken);
        }
    }
}
