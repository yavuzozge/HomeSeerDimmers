using System;
using System.Globalization;
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
        [property: JsonPropertyName("value")] JsonElement Value);

    /// <summary>
    /// Extension class for <see cref="ZwaveConfigParameters"/>
    /// </summary>
    public static class ZwaveConfigParametersExtensions
    {
        /// <summary>
        /// Reads a defined enum value from an enumerated Z-Wave configuration parameter.
        /// </summary>
        /// <typeparam name="T">Enum type.</typeparam>
        /// <param name="config">Z-Wave configuration parameter.</param>
        /// <param name="enumValue">The parsed value, or the default enum value when reading fails.</param>
        /// <returns>Whether the parameter contains a defined enum value.</returns>
        public static bool TryGetEnumeratedValue<T>(this ZwaveConfigParameters config, out T enumValue) where T : struct, Enum
        {
            enumValue = default;
            if (config == null || config.ValueType != "enumerated"
                || config.Value.ValueKind != JsonValueKind.Number
                || !config.Value.TryGetInt32(out int value))
            {
                return false;
            }

            if (Enum.TryParse(value.ToString(CultureInfo.InvariantCulture), out T candidate) && Enum.IsDefined(candidate))
            {
                enumValue = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads an integer from a configuration parameter.
        /// </summary>
        /// <param name="config">Z-Wave configuration parameter.</param>
        /// <param name="value">The integer value, or zero when reading fails.</param>
        /// <returns>Whether the parameter contains a valid integer.</returns>
        public static bool TryGetIntValue(this ZwaveConfigParameters config, out int value)
        {
            value = default;
            return config != null && config.ValueType == "manual_entry"
                && config.Value.ValueKind == JsonValueKind.Number
                && config.Value.TryGetInt32(out value);
        }
    }
}