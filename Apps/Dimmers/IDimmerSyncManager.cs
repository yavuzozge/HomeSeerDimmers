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
        /// Syncs dimmers to the LED input table
        /// </summary>
        /// <param name="input">LED input table</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Awaitable task</returns>
        Task SyncDimmersAsync(LedInputTable input, CancellationToken cancellationToken);
    }
}