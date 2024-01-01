using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Represents a Home Assistant command to set ZWave configuration parameters of a given device
    /// </summary>
    public record SetZWaveConfigParameterHaCommand : CommandMessage
    {
        public SetZWaveConfigParameterHaCommand()
        {
            Type = "zwave_js/set_config_parameter";
        }

        [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
        [JsonPropertyName("property")] public int Property { get; init; }
        [JsonPropertyName("property_key")] public int? PropertyKey { get; init; } = null;
        [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a ZWave configuration result status
    /// </summary>
    /// <param name="Status"></param>
    // ValueKind = Object : {"status":"accepted","result":{"data":{"status":254},"status":254,"remaining_duration":null,"message":null}}"
    public record ZwaveConfigSetResultStatus(
        [property: JsonPropertyName("status")] string Status
    );

    /// <summary>
    /// Represents a ZWave configuration result
    /// </summary>
    /// <param name="ValueId"></param>
    /// <param name="Status"></param>
    // ValueKind = Object : "{"value_id":"3 - 112 - 0 - 27","status":{"status":"accepted","result":{"data":{"status":254},"status":254,"remaining_duration":null,"message":null}}}"
    public record ZwaveConfigSetResult(
        [property: JsonPropertyName("value_id")] string ValueId,
        [property: JsonPropertyName("status")] ZwaveConfigSetResultStatus Status
    );

    /// <summary>
    /// Command connection extensions
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Sets config parameter of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="property">Property index</param>
        /// <param name="propertyKey">Property key</param>
        /// <param name="value">Property value</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the set operation fails</exception>
        private static async Task SetZWaveConfigParameterAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int property, int? propertyKey, string value, CancellationToken cancellationToken)
        {
            InvalidZWaveDeviceException.ThrowIfInvalid(device);

            // ValueKind = Object : "{"value_id":"3 - 112 - 0 - 27","status":{"status":"accepted","result":{"data":{"status":254},"status":254,"remaining_duration":null,"message":null}}}"
            ZwaveConfigSetResult result = await connection.SendCommandAndReturnResponseAsync<SetZWaveConfigParameterHaCommand, ZwaveConfigSetResult>(
                new SetZWaveConfigParameterHaCommand
                {
                    DeviceId = device.Id,
                    Property = property,
                    PropertyKey = propertyKey,
                    Value = value
                },
                cancellationToken) ?? throw new InvalidOperationException("Zwave config set result is null");

            if (result.Status == null)
            {
                throw new InvalidOperationException("Zwave config set result status is null");
            }

            if (!string.Equals(result.Status.Status, "accepted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Zwave config set result status does not indicate success: {result.Status.Status}");
            }
        }

        /// <summary>
        /// Sets config parameter of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="property">Property index</param>
        /// <param name="propertyKey">Property key</param>
        /// <param name="value">Property value</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the set operation fails</exception>
        public static async Task SetZWaveConfigParameterAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int property, int propertyKey, string value, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, property, (int?)propertyKey, value, cancellationToken);
        }

        /// <summary>
        /// Sets config parameter of a ZWave device
        /// </summary>
        /// <param name="connection">Home assistant connection</param>
        /// <param name="device">ZWave device</param>
        /// <param name="property">Property index</param>
        /// <param name="value">Property value</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        /// <exception cref="InvalidOperationException">Thrown when the set operation fails</exception>
        public static async Task SetZWaveConfigParameterAsync(this IHomeAssistantConnection connection, HassDeviceExtended device, int property, string value, CancellationToken cancellationToken)
        {
            await connection.SetZWaveConfigParameterAsync(device, property, null, value, cancellationToken);
        }
    }
}