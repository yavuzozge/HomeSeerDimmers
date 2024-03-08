using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Ozy.HomeSeerDimmers
{
    public static class CustomLoggingProvider
    {
        /// <summary>
        ///     Adds standard Serilog logging configuration, from appsettings, as per:
        ///     https://github.com/datalust/dotnet6-serilog-example
        /// </summary>
        /// <param name="builder"></param>
        public static IHostBuilder UseCustomLogging(this IHostBuilder builder)
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();

            Serilog.Core.Logger logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            return builder.UseSerilog(logger);
        }
    }
}
