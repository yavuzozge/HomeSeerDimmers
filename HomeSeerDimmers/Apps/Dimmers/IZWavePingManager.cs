using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Pings Z-Wave devices
    /// </summary>
    public interface IZWavePingManager
    {
        /// <summary>
        /// Pings Z-Wave devices that are configured in <see cref="Config"/>
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        Task PingDevicesAsync(CancellationToken cancellationToken);
    }
}