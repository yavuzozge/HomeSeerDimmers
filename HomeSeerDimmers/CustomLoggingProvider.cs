using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Ozy.HomeSeerDimmers
{
    public static class CustomLoggingProvider
    {
        /// <summary>
        /// Uses the host configuration for Serilog and supplies console output when no sinks are configured.
        /// </summary>
        /// <param name="builder">Host builder.</param>
        /// <returns>The host builder with Serilog configured.</returns>
        public static IHostBuilder UseCustomLogging(this IHostBuilder builder)
        {
            return builder.UseSerilog((context, configuration) =>
            {
                _ = configuration.ReadFrom.Configuration(context.Configuration);
                if (!context.Configuration.GetSection("Serilog:WriteTo").Exists())
                {
                    _ = configuration.WriteTo.Console();
                }
            });
        }
    }
}