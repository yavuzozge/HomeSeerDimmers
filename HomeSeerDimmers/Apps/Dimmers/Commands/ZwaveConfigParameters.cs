using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
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
    public record ZwaveConfigParameters(
        [property: JsonPropertyName("property")] int Property,
        [property: JsonPropertyName("property_key")] int? PropertyKey,
        [property: JsonPropertyName("configuration_value_type")] string ValueType,
        [property: JsonPropertyName("value")] dynamic Value);

    /// <summary>
    /// Extension class for <see cref="ZwaveConfigParameters"/>
    /// </summary>
    public static class ZwaveConfigParametersExtensions
    {
        /// <summary>
        /// Helper method to get an value as an enum from a ZWave configuration parameters
        /// </summary>
        /// <typeparam name="T">Type of enum</typeparam>
        /// <param name="config">ZWave configuration</param>
        /// <param name="enumValue">Output</param>
        /// <returns>True if get was succesful, otherwise false</returns>
        public static bool TryGetEnumeratedValue<T>(this ZwaveConfigParameters config, out T enumValue) where T : struct, Enum
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

        public static bool TryGetIntValue(this ZwaveConfigParameters config, out int value)
        {
            if (config == null)
            {
                value = default;
                return false;
            }
            if (config.ValueType != "manual_entry")
            {
                value = default;
                return false;
            }

            value = ((JsonElement)config.Value).GetInt32();
            return true;
        }
    }
}
