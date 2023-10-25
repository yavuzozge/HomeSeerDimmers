using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Connection extensions
    /// </summary>
    public static class ConnectionExtensions
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
        /// Gets extended Home Assistant devices from Home Assistant device registry
        /// </summary>
        /// <param name="connection">Connection</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Collection of Home Assistant devices</returns>
        public static async Task<IEnumerable<HassDeviceExtended>> GetDevicesExtendedAsync(this IHomeAssistantConnection connection, CancellationToken cancellationToken)
        {
            IReadOnlyCollection<HassDeviceExtended>? devices = await connection.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(new BasicHaCommand("config/device_registry/list"), cancellationToken);
            return devices ?? Enumerable.Empty<HassDeviceExtended>();
        }
    }
}
