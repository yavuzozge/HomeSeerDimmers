using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ozy.HomeSeerDimmers;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers default console output and logging configuration supplied by the host.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class CustomLoggingProviderTests
    {
        [TestMethod]
        public void MissingSinkConfigurationStillProducesConsoleOutput()
        {
            string output = CaptureLogOutput(new Dictionary<string, string?>());

            output.Should().Contain("Visible warning");
        }

        [TestMethod]
        public void ConfiguredConsoleOutputIsNotDuplicated()
        {
            string output = CaptureLogOutput(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "Console"
            });

            output.Split("Visible warning", StringSplitOptions.None).Should().HaveCount(2);
        }

        [TestMethod]
        public void HostConfigurationControlsMinimumLevels()
        {
            string output = CaptureLogOutput(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Warning"
            });

            output.Should().Contain("Visible warning").And.NotContain("Hidden information");
        }

        /// <summary>
        /// Captures console logs and restores the original writer so logging assertions do not affect other tests.
        /// </summary>
        /// <param name="settings">Host settings used to select log levels and output sinks.</param>
        /// <returns>The console text produced by the test host's logger.</returns>
        private static string CaptureLogOutput(Dictionary<string, string?> settings)
        {
            TextWriter original = Console.Out;
            using StringWriter output = new();
            Console.SetOut(output);
            try
            {
                using IHost host = new HostBuilder()
                    .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings))
                    .UseCustomLogging()
                    .Build();
                ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Logging test");
                logger.LogInformation("Hidden information");
                logger.LogWarning("Visible warning");
            }
            finally
            {
                Console.SetOut(original);
            }
            return output.ToString();
        }
    }
}