using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Host builder extensions that are used to indicate depenendencies of the app
    /// </summary>
    public static class HostBuilderExtensions
    {
        /// <summary>
        /// Indicates the depenendencies of the app
        /// </summary>
        /// <param name="hostBuilder">Host builder</param>
        /// <returns>Host builder</returns>
        public static IHostBuilder ConfigureHomeSeerApp(this IHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(delegate (HostBuilderContext context, IServiceCollection services)
            {
                _ = services.AddScoped<ILedInputMonitor, LedInputMonitor>();
                _ = services.AddScoped<IDimmerSyncManager, DimmerSyncManager>();
                _ = services.AddScoped<IZWavePingManager, ZWavePingManager>();
            });
        }
    }
}
