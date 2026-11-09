using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.Client;
using NetDaemon.Client.Exceptions;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System.Reactive.Concurrency;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers ping discovery, registry changes, cancellation, and device failures.
    /// </summary>
    [TestClass]
    public class ZWavePingManagerTests
    {
        /// <summary>
        /// Records refresh messages so tests can verify that missing devices and failures are reported.
        /// </summary>
        private readonly Mock<ILogger<ZWavePingManager>> logger = new();

        /// <summary>
        /// Supplies a controlled connection so refreshes can be tested without a Home Assistant server.
        /// </summary>
        private readonly Mock<IHomeAssistantRunner> runner = new();

        /// <summary>
        /// Supplies the test settings so device selection and discovery timing can change per scenario.
        /// </summary>
        private readonly Mock<IAppConfig<Config>> appConfig = new();

        /// <summary>
        /// Advances discovery time on demand so newly available devices can be tested without delays.
        /// </summary>
        private readonly HistoricalScheduler scheduler = new();

        /// <summary>
        /// Holds selected devices and cache timing so tests can reproduce registry changes and failures.
        /// </summary>
        private readonly Config config = new() { ZWaveDevicesDiscoveryValidity = TimeSpan.FromMinutes(10) };

        [TestInitialize]
        public void Initialize()
        {
            this.appConfig.Setup(c => c.Value).Returns(this.config);
            this.config.ZWavePingDevices.Add(new PingZwaveDevice { Name = "Lock", RefreshCommandClassId = ZWaveCommandClassId.DoorLock });
        }

        [TestMethod]
        public async Task OneFailedPingDoesNotPreventTheNextDeviceFromRefreshing()
        {
            HassDeviceExtended failed = ZWaveTestDevices.CreateDevice("failed", "Lock");
            HassDeviceExtended healthy = ZWaveTestDevices.CreateDevice("healthy", "Sensor");
            this.config.ZWavePingDevices.Add(new PingZwaveDevice { Name = "Sensor", RefreshCommandClassId = ZWaveCommandClassId.SensorBinary });
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(failed, healthy);
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == failed.Id), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HomeAssistantApiCallException("Device unavailable"));
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await this.CreateManager().PingDevicesAsync(CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == healthy.Id
                    && command.CommandClassId == (int)ZWaveCommandClassId.SensorBinary), CancellationToken.None), Times.Once);
            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        [TestMethod]
        public async Task MissingDevicesAreFoundAfterDiscoveryExpires()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice(name: "Lock");
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            connection.SetupSequence(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None))
                .ReturnsAsync(Array.Empty<HassDeviceExtended>())
                .ReturnsAsync(new[] { device });
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            ZWavePingManager manager = this.CreateManager();

            await manager.PingDevicesAsync(CancellationToken.None);
            this.scheduler.AdvanceBy(this.config.ZWaveDevicesDiscoveryValidity - TimeSpan.FromTicks(1));
            await manager.PingDevicesAsync(CancellationToken.None);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<RefreshNodeCcValuesHaCommand>(), CancellationToken.None), Times.Never);

            this.scheduler.AdvanceBy(TimeSpan.FromTicks(1));
            await manager.PingDevicesAsync(CancellationToken.None);
            connection.Verify(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None), Times.Exactly(2));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == device.Id), CancellationToken.None), Times.Once);
            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Warning));
        }

        [TestMethod]
        public async Task FailedPingsInvalidateDiscoveryAndFindReplacementRegistryIds()
        {
            HassDeviceExtended oldDevice = ZWaveTestDevices.CreateDevice("old", "Lock");
            HassDeviceExtended replacement = ZWaveTestDevices.CreateDevice("new", "Lock");
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(oldDevice, replacement);
            connection.SetupSequence(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None))
                .ReturnsAsync(new[] { oldDevice })
                .ReturnsAsync(new[] { replacement });
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == oldDevice.Id), CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("Device removed"));
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            ZWavePingManager manager = this.CreateManager();

            await manager.PingDevicesAsync(CancellationToken.None);
            await manager.PingDevicesAsync(CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == oldDevice.Id), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == replacement.Id), CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task ZeroValidityRediscoversOnEveryPing()
        {
            this.config.ZWaveDevicesDiscoveryValidity = TimeSpan.Zero;
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(ZWaveTestDevices.CreateDevice(name: "Lock"));
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            ZWavePingManager manager = this.CreateManager();

            await manager.PingDevicesAsync(CancellationToken.None);
            await manager.PingDevicesAsync(CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task DisabledAndNonZWaveDevicesAreNotPinged()
        {
            HassDeviceExtended disabled = ZWaveTestDevices.CreateDevice("disabled", "Lock") with { DisabledBy = "user" };
            HassDeviceExtended nonZWave = ZWaveTestDevices.CreateDevice("non-zwave", "Lock") with { Identifiers = new List<IList<string>>() };
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(disabled, nonZWave);
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await this.CreateManager().PingDevicesAsync(CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<RefreshNodeCcValuesHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CancellationDuringOnePingStopsTheRemainingDevices()
        {
            HassDeviceExtended first = ZWaveTestDevices.CreateDevice("first", "Lock");
            HassDeviceExtended second = ZWaveTestDevices.CreateDevice("second", "Lock");
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(first, second);
            using CancellationTokenSource cancellation = new();
            connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == first.Id), cancellation.Token))
                .Callback(cancellation.Cancel);
            this.runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);

            await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateManager().PingDevicesAsync(cancellation.Token));

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.DeviceId == second.Id), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task PreCancelledRequestsDoNotAccessHomeAssistant()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateManager().PingDevicesAsync(cancellation.Token));

            this.runner.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task MissingConnectionIsReported()
        {
            await this.CreateManager().PingDevicesAsync(CancellationToken.None);

            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        /// <summary>
        /// Creates a ping manager with the test services so refresh scenarios use a consistent setup.
        /// </summary>
        /// <returns>A manager that uses the test connection, settings, and clock.</returns>
        private ZWavePingManager CreateManager()
        {
            return new ZWavePingManager(this.logger.Object, this.runner.Object, this.appConfig.Object, this.scheduler);
        }
    }
}