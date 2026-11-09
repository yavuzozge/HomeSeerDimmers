using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Syncs dimmers
    /// </summary>
    public interface IDimmerSyncManager
    {
        /// <summary>
        /// Applies changed inputs using the Home Assistant cache to avoid polling every device on each sensor event.
        /// </summary>
        /// <param name="input">LED input table</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        Task SyncDimmersToAsync(LedInputTable input, CancellationToken cancellationToken);

        /// <summary>
        /// Reads physical device configuration before correcting differences, independently of the Home Assistant cache.
        /// </summary>
        /// <param name="input">Desired LED settings.</param>
        /// <param name="cancellationToken">Cancels device reads and writes.</param>
        /// <returns>A task that completes after reconciliation and its bounded retry.</returns>
        Task ReconcileDimmersToAsync(LedInputTable input, CancellationToken cancellationToken);
    }
}