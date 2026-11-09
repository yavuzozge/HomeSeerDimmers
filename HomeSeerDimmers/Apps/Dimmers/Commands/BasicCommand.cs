using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// A basic HA command class
    /// </summary>
    public record BasicHaCommand : CommandMessage
    {
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="type">Command type</param>
        public BasicHaCommand(string type)
        {
            Type = type;
        }
    }

    /// <summary>
    /// Command connection extensions
    /// </summary>
    public static partial class CommandConnectionExtensions
    {
        /// <summary>
        /// Gets extended Home Assistant devices from Home Assistant device registry
        /// </summary>
        /// <param name="connection">Connection</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Collection of Home Assistant devices</returns>
        public static async Task<IEnumerable<HassDeviceExtended>> GetDevicesExtendedAsync(this IHomeAssistantConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyCollection<HassDeviceExtended>? devices = await connection.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                new BasicHaCommand("config/device_registry/list"),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return devices ?? throw new InvalidOperationException("The Home Assistant device registry response is null");
        }
    }
}