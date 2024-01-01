using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Represents a Home Assistant command to refresh ZWave device values
    /// </summary>
    /// <remarks>
    /// See https://github.com/home-assistant/core/blob/dev/homeassistant/components/zwave_js/api.py
    /// </remarks>
    public record RefreshNodeValuesHaCommand : CommandMessage
    {
        public RefreshNodeValuesHaCommand()
        {
            Type = "zwave_js/refresh_node_values";
        }

        [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    }

    /// <summary>
    /// Command connection extensions
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Refreshes node values of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public static async Task RefreshNodeValuesAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            InvalidZWaveDeviceException.ThrowIfInvalid(device);

            await connection.SendCommandAndReturnResponseRawAsync(
                new RefreshNodeValuesHaCommand
                {
                    DeviceId = device.Id
                },
                cancellationToken);
        }
    }
}