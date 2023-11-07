using System.Collections.Generic;
using System.Linq;
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
        [property: JsonPropertyName("identifiers")] IList<IList<string>> Identifiers);

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
            if (device == null)
            {
                nodeId = 0;
                return false;
            }

            if (!device.Identifiers.Any())
            {
                nodeId = 0;
                return false;
            }

            if (device.Identifiers[0].Count < 2)
            {
                nodeId = 0;
                return false;
            }

            if (!string.Equals(device.Identifiers[0][0], "zwave_js"))
            {
                nodeId = 0;
                return false;
            }

            string[] splitted = device.Identifiers[0][1].Split('-');
            if (splitted.Length < 2)
            {
                nodeId = 0;
                return false;
            }

            return int.TryParse(splitted[1], out nodeId);
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
