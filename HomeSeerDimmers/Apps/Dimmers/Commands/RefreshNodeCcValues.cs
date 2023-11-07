using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Represents a Home Assistant command to refresh Command Class values of a ZWave device
    /// </summary>
    /// <remarks>
    /// See https://github.com/home-assistant/core/blob/dev/homeassistant/components/zwave_js/api.py
    /// </remarks>
    public record RefreshNodeCcValuesHaCommand : CommandMessage
    {
        public RefreshNodeCcValuesHaCommand()
        {
            Type = "zwave_js/refresh_node_cc_values";
        }

        [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
        [JsonPropertyName("command_class_id")] public int CommandClassId { get; init; }
    }

    /// <summary>
    /// Command connection extensions
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Refreshes command class values of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="commandClassId">ZWave command class ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public static async Task RefreshNodeCcValuesAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, ZWaveCommandClassId commandClassId, CancellationToken cancellationToken)
        {
            InvalidZWaveDeviceException.ThrowIfInvalid(device);

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
