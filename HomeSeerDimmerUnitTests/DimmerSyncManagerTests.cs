using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.Client.Exceptions;
using NetDaemon.Client.HomeAssistant.Model;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Reactive.Concurrency;
using System.Text.Json;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers discovery, LED writes, bounded retries, and device failure isolation.
    /// </summary>
    [TestClass]
    public class DimmerSyncManagerTests
    {
        /// <summary>
        /// Records sync messages so tests can verify that device and connection failures are reported.
        /// </summary>
        private readonly Mock<ILogger<DimmerSyncManager>> logger = new();

        /// <summary>
        /// Supplies the test settings so discovery timing and blink frequency can be changed per scenario.
        /// </summary>
        private readonly Mock<IAppConfig<Config>> appConfig = new();

        /// <summary>
        /// Supplies a controlled connection so synchronization can be tested without a Home Assistant server.
        /// </summary>
        private readonly Mock<IHomeAssistantRunner> runner = new();

        /// <summary>
        /// Advances discovery time on demand so cache boundaries can be tested without waiting.
        /// </summary>
        private readonly HistoricalScheduler scheduler = new();

        /// <summary>
        /// Holds discovery and LED settings that tests can adjust to exercise cache reuse and changed values.
        /// </summary>
        private readonly Config config = new() { ZWaveDevicesDiscoveryValidity = TimeSpan.FromMinutes(10) };

        [TestInitialize]
        public void Initialize()
        {
            this.appConfig.Setup(c => c.Value).Returns(this.config);
        }

        [TestMethod]
        public async Task ADeviceReadFailureDoesNotPreventHealthyDevicesFromSynchronizing()
        {
            HassDeviceExtended failed = ZWaveTestDevices.CreateDevice("failed");
            HassDeviceExtended healthy = ZWaveTestDevices.CreateDevice("healthy", nodeId: 38);
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(failed, healthy);
            Dictionary<string, ZwaveConfigParameters> healthyConfiguration = ZWaveTestDevices.CreateConfiguration(healthy);
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == failed.Id), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Device was removed"));
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == healthy.Id), It.IsAny<CancellationToken>()))
                .ReturnsAsync(healthyConfiguration);
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<SetZWaveConfigParameterHaCommand>(), CancellationToken.None))
                .ReturnsAsync((SetZWaveConfigParameterHaCommand command, CancellationToken _) =>
                {
                    if (command.DeviceId == healthy.Id && command.Property == 27 && command.PropertyKey == null)
                    {
                        healthyConfiguration["38-112-0-27"] = healthyConfiguration["38-112-0-27"] with
                        {
                            Value = JsonSerializer.SerializeToElement(command.Value)
                        };
                    }
                    return JsonSerializer.SerializeToElement(new { status = "accepted" });
                });
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await this.CreateManager().SyncDimmersToAsync(new LedInputTable().CreateWithColor(6, LedStatusColor.White), CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.DeviceId == healthy.Id
                    && command.Property == 27 && command.PropertyKey == null && command.Value == 7), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), CancellationToken.None), Times.Once);
            healthyConfiguration["38-112-0-27"].TryGetEnumeratedValue(out LedStatusColor color).Should().BeTrue();
            color.Should().Be(LedStatusColor.White);
            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        [TestMethod]
        [DataRow(21)]
        [DataRow(31)]
        [DataRow(13)]
        [DataRow(30)]
        public async Task FailedWritesAreRetriedOnlyOnce(int property)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            if (property == 13)
            {
                configuration["37-112-0-13"] = new ZwaveConfigParameters(13, null, "enumerated", JsonSerializer.SerializeToElement(0));
            }
            if (property == 30)
            {
                this.config.DimmerLedBlinkFrequency = 10;
            }
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<SetZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToElement(new { status = "rejected" }));
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            LedInputTable input = new();
            if (property == 21)
            {
                input = input.CreateWithColor(0, LedStatusColor.Red);
            }
            if (property == 31)
            {
                input = input.CreateWithBlink(0, LedBlink.On);
            }
            await this.CreateManager().SyncDimmersToAsync(input, CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.Property == property), CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task AWriteRetryConvergesAndTheNextSyncDoesNotWriteAgain()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);
            int attempts = 0;
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<SetZWaveConfigParameterHaCommand>(), CancellationToken.None))
                .ReturnsAsync((SetZWaveConfigParameterHaCommand command, CancellationToken _) =>
                {
                    if (++attempts == 1)
                    {
                        throw new InvalidOperationException("Dropped message");
                    }
                    if (command.DeviceId == device.Id && command.Property == 21 && command.PropertyKey == null)
                    {
                        configuration["37-112-0-21"] = configuration["37-112-0-21"] with
                        {
                            Value = JsonSerializer.SerializeToElement(command.Value)
                        };
                    }
                    return JsonSerializer.SerializeToElement(new { status = "accepted" });
                });
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            DimmerSyncManager manager = this.CreateManager();
            LedInputTable desired = new LedInputTable().CreateWithColor(0, LedStatusColor.Red);

            await manager.SyncDimmersToAsync(desired, CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.DeviceId == device.Id
                    && command.Property == 21 && command.PropertyKey == null && command.Value == 1), CancellationToken.None), Times.Exactly(2));
            configuration["37-112-0-21"].TryGetEnumeratedValue(out LedStatusColor color).Should().BeTrue();
            color.Should().Be(LedStatusColor.Red);

            await manager.SyncDimmersToAsync(desired, CancellationToken.None);

            attempts.Should().Be(2);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task OnlyChangedColorsBlinkModeAndFrequencyAreWritten()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            configuration["37-112-0-13"] = new ZwaveConfigParameters(13, null, "enumerated", JsonSerializer.SerializeToElement(0));
            configuration["37-112-0-30"] = new ZwaveConfigParameters(30, null, "manual_entry", JsonSerializer.SerializeToElement(4));
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            this.config.DimmerLedBlinkFrequency = 10;

            await this.CreateManager().SyncDimmersToAsync(new LedInputTable()
                .CreateWithColor(6, LedStatusColor.White).CreateWithBlink(6, LedBlink.On), CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), CancellationToken.None), Times.Exactly(4));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.Property == 27 && command.Value == 7), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.Property == 31 && command.PropertyKey == 64 && command.Value == 1), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.Property == 13 && command.Value == 1), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.Property == 30 && command.Value == 10), CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task MatchingConfigurationsDoNotGenerateWrites()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await this.CreateManager().SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<CommandMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        [DataRow("HS-WD200+")]
        [DataRow("HS-WX300")]
        public async Task DiscoveryExcludesDisabledUnsupportedAndNonZWaveDevices(string supportedModel)
        {
            HassDeviceExtended supported = ZWaveTestDevices.CreateDevice("supported") with { Model = supportedModel };
            HassDeviceExtended disabled = supported with { Id = "disabled", DisabledBy = "user" };
            HassDeviceExtended unsupported = supported with { Id = "unsupported", Model = "Other" };
            HassDeviceExtended nonZWave = supported with { Id = "non-zwave", Identifiers = new List<IList<string>>() };
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(supported, disabled, unsupported, nonZWave);
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await this.CreateManager().SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == supported.Id), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId != supported.Id), CancellationToken.None), Times.Never);
        }

        [TestMethod]
        public async Task DiscoveryIsReusedUntilItsExactExpiry()
        {
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection();
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            DimmerSyncManager manager = this.CreateManager();

            await manager.SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);
            this.scheduler.AdvanceBy(this.config.ZWaveDevicesDiscoveryValidity - TimeSpan.FromTicks(1));
            await manager.SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);
            connection.Verify(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None), Times.Once);

            this.scheduler.AdvanceBy(TimeSpan.FromTicks(1));
            await manager.SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);
            connection.Verify(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task FailedDiscoveryDoesNotPoisonTheCache()
        {
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection();
            connection.SetupSequence(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None))
                .ThrowsAsync(new HomeAssistantApiCallException("Registry unavailable"))
                .ReturnsAsync(Array.Empty<HassDeviceExtended>());
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            DimmerSyncManager manager = this.CreateManager();

            await Assert.ThrowsAsync<HomeAssistantApiCallException>(() => manager.SyncDimmersToAsync(new LedInputTable(), CancellationToken.None));
            await manager.SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task CancellationDuringOneDeviceStopsTheRemainingDevices()
        {
            HassDeviceExtended first = ZWaveTestDevices.CreateDevice("first");
            HassDeviceExtended second = ZWaveTestDevices.CreateDevice("second");
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(first, second);
            using CancellationTokenSource cancellation = new();
            connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == first.Id), cancellation.Token))
                .Callback(cancellation.Cancel)
                .ReturnsAsync(ZWaveTestDevices.CreateConfiguration(first));
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateManager().SyncDimmersToAsync(new LedInputTable(), cancellation.Token));

            connection.Verify(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == second.Id), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task PreCancelledRequestsDoNotAccessHomeAssistant()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateManager().SyncDimmersToAsync(new LedInputTable(), cancellation.Token));

            this.runner.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task MissingConnectionIsReported()
        {
            await this.CreateManager().SyncDimmersToAsync(new LedInputTable(), CancellationToken.None);

            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        /// <summary>
        /// Creates a sync manager with the current test services so all scenarios use the same controlled clock.
        /// </summary>
        /// <returns>A manager that uses the test connection, configuration, and logger.</returns>
        private DimmerSyncManager CreateManager()
        {
            return new DimmerSyncManager(this.logger.Object, this.appConfig.Object, this.runner.Object, this.scheduler);
        }
    }
}