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
        /// Pings discovered Z-Wave devices
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        Task PingDevicesAsync(CancellationToken cancellationToken);
    }
}