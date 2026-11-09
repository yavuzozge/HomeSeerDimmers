using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Data class represeting a Home Assistant device that captures additional properties
    /// compared to <see cref="NetDaemon.Client.HomeAssistant.Model.HassDevice"/> such as identifiers
    /// </summary>
    /// <param name="Id"></param>
    /// <param name="AreaId"></param>
    /// <param name="Name"></param>
    /// <param name="NameByUser"></param>
    /// <param name="Manufacturer"></param>
    /// <param name="Model"></param>
    /// <param name="Identifiers"></param>
    //{
    //    "area_id": "living_room",
    //    "configuration_url": null,
    //    "config_entries": [
    //        "54b16ed2deab40ee36a1a90b3574c552"
    //    ],
    //    "connections": [],
    //    "disabled_by": null,
    //    "entry_type": null,
    //    "hw_version": null,
    //    "id": "8880d29566218ee0f2d57ab7ba928d7c",
    //    "identifiers": [
    //        [
    //            "zwave_js",
    //            "4023755774-37"
    //        ],
    //        [
    //            "zwave_js",
    //            "4023755774-37-12:17479:12342"
    //        ]
    //    ],
    //    "manufacturer": "HomeSeer Technologies",
    //    "model": "HS-WD200+",
    //    "name_by_user": null,
    //    "name": "Fireplace Lights",
    //    "sw_version": "5.14",
    //    "via_device_id": "0a250010aa0b33a9cbf66a6cffed2403"
    //},
    public record HassDeviceExtended(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("area_id")] string AreaId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("name_by_user")] string NameByUser,
        [property: JsonPropertyName("manufacturer")] string Manufacturer,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("identifiers")] IList<IList<string>> Identifiers)
    {
        /// <summary>
        /// Identifies who disabled the device, or null when the device is enabled.
        /// </summary>
        [JsonPropertyName("disabled_by")]
        public string? DisabledBy { get; init; }
    }

    /// <summary>
    /// ZWave extensions to <see cref="HassDeviceExtended"/>
    /// </summary>
    public static class HassDeviceExtendedZWaveExtensions
    {
        /// <summary>
        /// Returns ZWave node ID
        /// </summary>
        /// <param name="device">Home Assistant device</param>
        /// <param name="nodeId">ZWave node ID</param>
        /// <returns>True if return had succeeded, false otherwise</returns>
        public static bool TryGetZWaveNodeId(this HassDeviceExtended device, out int nodeId)
        {
            nodeId = 0;
            if (device?.Identifiers == null)
            {
                return false;
            }

            foreach (IList<string> identifier in device.Identifiers)
            {
                if (identifier == null || identifier.Count != 2
                    || !string.Equals(identifier[0], "zwave_js", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(identifier[1]))
                {
                    continue;
                }

                ReadOnlySpan<char> value = identifier[1];
                int separator = value.IndexOf('-');
                if (separator <= 0
                    || !uint.TryParse(value[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out uint homeId)
                    || homeId == 0)
                {
                    continue;
                }

                ReadOnlySpan<char> nodeAndSuffix = value[(separator + 1)..];
                int suffixSeparator = nodeAndSuffix.IndexOf('-');
                ReadOnlySpan<char> node = suffixSeparator < 0 ? nodeAndSuffix : nodeAndSuffix[..suffixSeparator];
                if (int.TryParse(node, NumberStyles.None, CultureInfo.InvariantCulture, out int candidate)
                    && candidate > 0
                    && (suffixSeparator < 0 || IsValidIdentifierSuffix(nodeAndSuffix[(suffixSeparator + 1)..])))
                {
                    nodeId = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks the product, endpoint, or endpoint-group suffix used by Home Assistant.
        /// </summary>
        /// <param name="suffix">Identifier suffix after the node ID.</param>
        /// <returns>Whether the suffix has a supported numeric format.</returns>
        private static bool IsValidIdentifierSuffix(ReadOnlySpan<char> suffix)
        {
            if (suffix.StartsWith("g", StringComparison.Ordinal))
            {
                return int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int groupId) && groupId > 0;
            }

            if (!suffix.Contains(':'))
            {
                return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int endpoint) && endpoint > 0;
            }

            Span<Range> parts = stackalloc Range[4];
            return suffix.Split(parts, ':') == 3
                && ushort.TryParse(suffix[parts[0]], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && ushort.TryParse(suffix[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && ushort.TryParse(suffix[parts[2]], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>
        /// Returns ZWave node ID or throws <see cref="InvalidZWaveDeviceException"/>
        /// </summary>
        /// <param name="device">Home Assistant device</param>
        /// <returns>ZWave node ID</returns>
        public static int GetZWaveNodeId(this HassDeviceExtended device)
        {
            if (!device.TryGetZWaveNodeId(out int zwaveNodeId))
            {
                throw new InvalidZWaveDeviceException();
            }

            return zwaveNodeId;
        }
    }
}