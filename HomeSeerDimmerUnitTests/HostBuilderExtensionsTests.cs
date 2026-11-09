using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.HassModel;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using System.Reactive.Concurrency;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Verifies that the existing host registrations resolve the scoped application services.
    /// </summary>
    [TestClass]
    public class HostBuilderExtensionsTests
    {
        [TestMethod]
        public void ApplicationServicesResolveWithinTheirOwnScopes()
        {
            Mock<IAppConfig<Config>> appConfig = new();
            _ = appConfig.SetupGet(c => c.Value).Returns(new Config
            {
                DimmerLedColorEntityNamePattern = "sensor.dimmer_led_{0}_color",
                DimmerLedBlinkEntityNamePattern = "binary_sensor.dimmer_led_{0}_blink"
            });
            using IHost host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    _ = services.AddSingleton(appConfig.Object);
                    _ = services.AddSingleton(Mock.Of<IHomeAssistantRunner>());
                    _ = services.AddSingleton(Mock.Of<IHaContext>());
                    _ = services.AddSingleton<IScheduler>(new HistoricalScheduler());
                })
                .ConfigureHomeSeerApp()
                .Build();
            using IServiceScope first = host.Services.CreateScope();
            using IServiceScope second = host.Services.CreateScope();

            _ = first.ServiceProvider.GetRequiredService<ILedInputMonitor>().Should().BeOfType<LedInputMonitor>();
            _ = first.ServiceProvider.GetRequiredService<IZWavePingManager>().Should().BeOfType<ZWavePingManager>();
            IDimmerSyncManager manager = first.ServiceProvider.GetRequiredService<IDimmerSyncManager>();
            _ = manager.Should().BeOfType<DimmerSyncManager>();
            _ = first.ServiceProvider.GetRequiredService<IDimmerSyncManager>().Should().BeSameAs(manager);
            _ = second.ServiceProvider.GetRequiredService<IDimmerSyncManager>().Should().NotBeSameAs(manager);
        }
    }
}