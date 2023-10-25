using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Extension methods for <see cref="IHomeAssistantConnection"/> for Z-Wave devices
    /// </summary>
    public static class ConnectionZWaveExtensions
    {
        /// <summary>
        /// Represents a Home Assistant command to get ZWave configuration parameters of a given device
        /// </summary>
        private record GetZwaveConfigParametersHaCommand : CommandMessage
        {
            public GetZwaveConfigParametersHaCommand()
            {
                Type = "zwave_js/get_config_parameters";
            }

            /// <summary>
            /// Device ID
            /// </summary>
            [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
        }

        /// <summary>
        /// Represent ZWave config parameters
        /// </summary>
        /// <param name="Property"></param>
        /// <param name="PropertyKey"></param>
        /// <param name="ValueType"></param>
        /// <param name="Value"></param>
        //"37-112-0-21": {
        //    "property": 21,
        //    "property_key": null,
        //    "configuration_value_type": "enumerated",
        //    "metadata": {
        //        "description": null,
        //        "label": "Status LED 1 Color",
        //        "type": "number",
        //        "min": 0,
        //        "max": 7,
        //        "unit": null,
        //        "writeable": true,
        //        "readable": true,
        //        "states": {
        //            "0": "Off",
        //            "1": "Red",
        //            "2": "Green",
        //            "3": "Blue",
        //            "4": "Magenta",
        //            "5": "Yellow",
        //            "6": "Cyan",
        //            "7": "White"
        //        }
        //    },
        //    "value": 1
        //},

        //"37-112-0-31-1": {
        //    "property": 31,
        //    "property_key": 1,
        //    "configuration_value_type": "enumerated",
        //    "metadata": {
        //        "description": null,
        //        "label": "LED 1 Blink Status",
        //        "type": "number",
        //        "min": 0,
        //        "max": 1,
        //        "unit": null,
        //        "writeable": true,
        //        "readable": true,
        //        "states": {
        //            "0": "Disable",
        //            "1": "Enable"
        //        }
        //    },
        //    "value": 0
        //},
        private record ZwaveConfigParameters(
            [property: JsonPropertyName("property")] int Property,
            [property: JsonPropertyName("property_key")] int? PropertyKey,
            [property: JsonPropertyName("configuration_value_type")] string ValueType,
            [property: JsonPropertyName("value")] dynamic Value);

        /// <summary>
        /// Represents a Home Assistant command to set ZWave configuration parameters of a given device
        /// </summary>
        private record SetZwaveConfigParameterHaCommand : CommandMessage
        {
            public SetZwaveConfigParameterHaCommand()
            {
                Type = "zwave_js/set_config_parameter";
            }

            [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
            [JsonPropertyName("property")] public int Property { get; init; }
            [JsonPropertyName("property_key")] public int? PropertyKey { get; init; } = null;
            [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
        }

        /// <summary>
        /// Represents a ZWave configuration result
        /// </summary>
        /// <param name="ValueId"></param>
        /// <param name="Status"></param>
        // ValueKind = Object : "{"value_id":"37-112-0-21","status":"accepted"}"
        private record ZwaveConfigSetResult(
            [property: JsonPropertyName("value_id")] string ValueId,
            [property: JsonPropertyName("status")] string Status
        );

        /// <summary>
        /// Represents a Home Assistant command to refresh ZWave device values
        /// </summary>
        /// <remarks>
        /// See https://github.com/home-assistant/core/blob/dev/homeassistant/components/zwave_js/api.py
        /// </remarks>
        private record RefreshNodeValuesHaCommand : CommandMessage
        {
            public RefreshNodeValuesHaCommand()
            {
                Type = "zwave_js/refresh_node_values";
            }

            [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
        }

        /// <summary>
        /// Represents a Home Assistant command to refresh Command Class values of a ZWave device
        /// </summary>
        /// <remarks>
        /// See https://github.com/home-assistant/core/blob/dev/homeassistant/components/zwave_js/api.py
        /// </remarks>
        private record RefreshNodeCcValuesHaCommand : CommandMessage
        {
            public RefreshNodeCcValuesHaCommand()
            {
                Type = "zwave_js/refresh_node_cc_values";
            }

            [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
            [JsonPropertyName("command_class_id")] public int CommandClassId { get; init; }
        }

        /// <summary>
        /// Helper method to get an value as an enum from a ZWave configuration parameters
        /// </summary>
        /// <typeparam name="T">Type of enum</typeparam>
        /// <param name="config">ZWave configuration</param>
        /// <param name="enumValue">Output</param>
        /// <returns>True if get was succesful, otherwise false</returns>
        private static bool TryGetEnumeratedValue<T>(ZwaveConfigParameters config, out T enumValue) where T : struct, Enum
        {
            if (config == null)
            {
                enumValue = default;
                return false;
            }
            if (config.ValueType != "enumerated")
            {
                enumValue = default;
                return false;
            }

            int value = ((JsonElement)config.Value).GetInt32();
            T[] values = Enum.GetValues<T>();
            T? found = values.FirstOrDefault(v => Convert.ToInt32(v) == value);
            if (!found.HasValue)
            {
                enumValue = default;
                return false;
            }
            {
                enumValue = found.Value;
                return true;
            }
        }

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
            if (!device.TryGetZWaveNodeId(out int zwaveNodeId))
            {
                throw new InvalidOperationException("Device is not a Z-Wave device");
            }

            IDictionary<string, ZwaveConfigParameters>? result = await connection.SendCommandAndReturnResponseAsync<GetZwaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                new GetZwaveConfigParametersHaCommand { DeviceId = device.Id },
                cancellationToken);
            if (result == null)
            {
                return Array.Empty<(LedStatusColor color, LedBlink blink)>();
            }

            (LedStatusColor color, LedBlink blink)[] configs = new (LedStatusColor color, LedBlink blink)[7];
            for (int i = 0; i < configs.Length; ++i)
            {
                if (!result.TryGetValue(CreateLedStatusColorConfigName(zwaveNodeId, i), out ZwaveConfigParameters? config) || !TryGetEnumeratedValue(config, out LedStatusColor color))
                {
                    throw new InvalidOperationException($"Failed to parse LED color: {i} for device {device.Name}");
                }

                if (!result.TryGetValue(CreateLedStatusBlinkConfigName(zwaveNodeId, i), out config) || !TryGetEnumeratedValue(config, out LedBlink blink))
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
            if (!device.TryGetZWaveNodeId(out int _))
            {
                throw new InvalidOperationException("Device is not a Z-Wave device");
            }

            // ValueKind = Object : "{"value_id":"37-112-0-21","status":"accepted"}"
            ZwaveConfigSetResult? resultColor = await connection.SendCommandAndReturnResponseAsync<SetZwaveConfigParameterHaCommand, ZwaveConfigSetResult>(
                new SetZwaveConfigParameterHaCommand
                {
                    DeviceId = device.Id,
                    Property = 21 + index,
                    Value = ((int)color).ToString()
                },
                cancellationToken);

            if (!string.Equals(resultColor?.Status, "accepted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Operation result status is not success: {resultColor?.Status}");
            }
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
            if (!device.TryGetZWaveNodeId(out int _))
            {
                throw new InvalidOperationException("Device is not a Z-Wave device");
            }

            // ValueKind = Object : "{"value_id":"37-112-0-31-1","status":"accepted"}"
            // ValueKind = Object : "{"value_id":"37-112-0-31-2","status":"accepted"}"
            // ValueKind = Object : "{"value_id":"37-112-0-31-4","status":"accepted"}"
            ZwaveConfigSetResult? resultBlink = await connection.SendCommandAndReturnResponseAsync<SetZwaveConfigParameterHaCommand, ZwaveConfigSetResult>(
                new SetZwaveConfigParameterHaCommand
                {
                    DeviceId = device.Id,
                    Property = 31,
                    PropertyKey = 1 << index,
                    Value = blink == LedBlink.On ? "1" : "0"
                },
                cancellationToken);

            if (!string.Equals(resultBlink?.Status, "accepted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Operation result status is not success: {resultBlink?.Status}");
            }
        }

        /// <summary>
        /// Refreshes node values of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        public static async Task RefreshNodeValuesAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            if (!device.TryGetZWaveNodeId(out int _))
            {
                throw new InvalidOperationException("Device is not a Z-Wave device");
            }

            await connection.SendCommandAndReturnResponseRawAsync(
                new RefreshNodeValuesHaCommand
                {
                    DeviceId = device.Id
                },
                cancellationToken);
        }

        /// <summary>
        /// Refreshes command class values of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="commandClassId">ZWave command class ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <exception cref="InvalidOperationException">Thrown when the device is not a ZWave device</exception>
        public static async Task RefreshNodeCcValuesAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, ZWaveCommandClassId commandClassId, CancellationToken cancellationToken)
        {
            if (!device.TryGetZWaveNodeId(out int _))
            {
                throw new InvalidOperationException("Device is not a Z-Wave device");
            }

            await connection.SendCommandAndReturnResponseRawAsync(
                new RefreshNodeCcValuesHaCommand
                {
                    DeviceId = device.Id,
                    CommandClassId = (int)commandClassId
                },
                cancellationToken);
        }
    }
}
