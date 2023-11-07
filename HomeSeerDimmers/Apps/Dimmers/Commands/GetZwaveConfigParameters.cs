using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Represents a Home Assistant command to get ZWave configuration parameters of a given device
    /// </summary>
    public record GetZWaveConfigParametersHaCommand : CommandMessage
    {
        public GetZWaveConfigParametersHaCommand()
        {
            Type = "zwave_js/get_config_parameters";
        }

        /// <summary>
        /// Device ID
        /// </summary>
        [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    }

    /// <summary>
    /// Command connection extensions
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Gets config parameteters of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A dictionary containing the parameters</returns>
        public static async Task<IDictionary<string, ZwaveConfigParameters>> GetZWaveConfigParametersAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, CancellationToken cancellationToken)
        {
            InvalidZWaveDeviceException.ThrowIfInvalid(device);

            IDictionary<string, ZwaveConfigParameters>? result = await connection.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                new GetZWaveConfigParametersHaCommand
                {
                    DeviceId = device.Id
                },
                cancellationToken);

            return result ?? new Dictionary<string, ZwaveConfigParameters>();
        }
    }
}
