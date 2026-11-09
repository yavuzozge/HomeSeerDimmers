using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Requests a configuration value from the physical node rather than its cached metadata.
    /// </summary>
    public record GetRawZWaveConfigParameterHaCommand : CommandMessage
    {
        public GetRawZWaveConfigParameterHaCommand()
        {
            Type = "zwave_js/get_raw_config_parameter";
        }

        [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
        [JsonPropertyName("property")] public int Property { get; init; }
    }

    /// <summary>
    /// Command connection extensions.
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Waits for a physical configuration read and rejects missing or malformed responses.
        /// </summary>
        /// <param name="connection">Current Home Assistant connection.</param>
        /// <param name="device">Node whose parameter is read.</param>
        /// <param name="property">Complete configuration parameter number.</param>
        /// <param name="cancellationToken">Cancels the device read.</param>
        /// <returns>The complete integer parameter value returned by the device.</returns>
        public static async Task<int> GetRawZWaveConfigParameterAsync(
            this IHomeAssistantConnection connection, HassDeviceExtended device, int property, CancellationToken cancellationToken)
        {
            InvalidZWaveDeviceException.ThrowIfInvalid(device);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(property);
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement? result = await connection.SendCommandAndReturnResponseRawAsync(
                new GetRawZWaveConfigParameterHaCommand { DeviceId = device.Id, Property = property }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not { ValueKind: JsonValueKind.Object } response
                || !response.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out int parameter))
            {
                throw new InvalidOperationException($"Invalid physical configuration response for {device.Name}, parameter {property}: {result}");
            }
            return parameter;
        }
    }
}