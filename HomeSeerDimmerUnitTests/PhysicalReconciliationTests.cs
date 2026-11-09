using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Text.Json;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers completed device reads, stale caches, and configuration rejected before physical side effects.
    /// </summary>
    [TestClass]
    public class PhysicalReconciliationTests
    {
        [TestMethod]
        public async Task FullResyncRepairsTheDeviceEvenWhenTheHaCacheAlreadyMatches()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);
            Dictionary<string, ZwaveConfigParameters> cache = ZWaveTestDevices.CreateConfiguration(device);
            cache["37-112-0-21"] = cache["37-112-0-21"] with { Value = JsonSerializer.SerializeToElement(1) };
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(cache);
            DimmerSyncManager manager = CreateManager(connection);
            LedInputTable desired = new LedInputTable().CreateWithColor(0, LedStatusColor.Red);

            await manager.SyncDimmersToAsync(desired, CancellationToken.None);

            _ = physical[21].Should().Be(0);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
            connection.Invocations.Clear();

            await manager.ReconcileDimmersToAsync(desired, CancellationToken.None);
            _ = physical[21].Should().Be(1);
            await manager.ReconcileDimmersToAsync(desired, CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.DeviceId == device.Id), CancellationToken.None), Times.Exactly(20));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.Property == 31), CancellationToken.None), Times.Exactly(2));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<SetZWaveConfigParameterHaCommand>(command => command.DeviceId == device.Id
                    && command.Property == 21 && command.Value == 1), CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task ReconciliationWaitsForTheLastPhysicalResponseBeforeWriting()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<JsonElement?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.Property == 30), CancellationToken.None))
                .Callback(() => entered.SetResult()).Returns(response.Task);
            Task reconciliation = CreateManager(connection).ReconcileDimmersToAsync(
                new LedInputTable().CreateWithColor(0, LedStatusColor.Red), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                _ = reconciliation.IsCompleted.Should().BeFalse();
                connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                    It.IsAny<SetZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                _ = response.TrySetResult(JsonSerializer.SerializeToElement(new { value = 5 }));
            }

            await reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            _ = physical[21].Should().Be(1);
        }

        [TestMethod]
        public async Task APhysicalReadFailureDoesNotBlockHealthyDevicesOrRetryThem()
        {
            HassDeviceExtended failed = ZWaveTestDevices.CreateDevice("failed");
            HassDeviceExtended healthy = ZWaveTestDevices.CreateDevice("healthy", nodeId: 38);
            Dictionary<int, int> physical = CreatePhysicalParameters();
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(healthy, physical);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), CancellationToken.None)).ReturnsAsync(new[] { failed, healthy });
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.DeviceId == failed.Id), CancellationToken.None))
                .ThrowsAsync(new TimeoutException("Device did not respond"));

            await CreateManager(connection).ReconcileDimmersToAsync(
                new LedInputTable().CreateWithColor(0, LedStatusColor.Red), CancellationToken.None);

            _ = physical[21].Should().Be(1);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.DeviceId == failed.Id), CancellationToken.None), Times.Exactly(2));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.DeviceId == healthy.Id), CancellationToken.None), Times.Exactly(10));
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        public async Task PhysicalReadsPreserveEachLedColorAndBlinkPosition(int index)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            physical[21 + index] = 7;
            physical[31] = 1 << index;
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);

            DimmerConfig configuration = await connection.Object.ReadZWaveDimmerConfigAsync(device, CancellationToken.None);

            _ = configuration.Colors.Should().Equal(Enumerable.Range(0, 7).Select(i => i == index ? LedStatusColor.White : LedStatusColor.Off));
            _ = configuration.Blinks.Should().Equal(Enumerable.Range(0, 7).Select(i => i == index ? LedBlink.On : LedBlink.Off));
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), CancellationToken.None), Times.Exactly(10));
        }

        [TestMethod]
        [DataRow(21, 8)]
        [DataRow(31, -1)]
        [DataRow(31, 128)]
        [DataRow(13, 2)]
        [DataRow(30, -1)]
        [DataRow(30, 256)]
        public async Task InvalidPhysicalSettingsAreReportedBeforeAnyWrite(int property, int value)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            physical[property] = value;
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.Object.ReadZWaveDimmerConfigAsync(device, CancellationToken.None));

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        [DataRow("null")]
        [DataRow("{}")]
        [DataRow("{\"value\":null}")]
        [DataRow("{\"value\":\"1\"}")]
        [DataRow("{\"value\":1.5}")]
        [DataRow("{\"value\":2147483648}")]
        [DataRow("[]")]
        public async Task MalformedPhysicalReadResponsesAreExplicitFailures(string json)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.Deserialize<JsonElement>(json));

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.Object.GetRawZWaveConfigParameterAsync(device, 21, CancellationToken.None));
        }

        [TestMethod]
        public async Task RawReadUsesTheHaWireContractAndPropagatesItsCancellationToken()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = new();
            GetRawZWaveConfigParameterHaCommand? captured = null;
            using CancellationTokenSource cancellation = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), cancellation.Token))
                .Callback((GetRawZWaveConfigParameterHaCommand command, CancellationToken _) => captured = command)
                .ReturnsAsync(JsonSerializer.Deserialize<JsonElement>("{\"value\":7}"));

            int value = await connection.Object.GetRawZWaveConfigParameterAsync(device, 27, cancellation.Token);

            _ = value.Should().Be(7);
            JsonElement json = JsonSerializer.SerializeToElement(captured);
            _ = json.GetProperty("type").GetString().Should().Be("zwave_js/get_raw_config_parameter");
            _ = json.GetProperty("device_id").GetString().Should().Be(device.Id);
            _ = json.GetProperty("property").GetInt32().Should().Be(27);
        }

        [TestMethod]
        public async Task CancellingReconciliationWhileAReadIsPendingPreventsAllWrites()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<JsonElement?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource cancellation = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<GetRawZWaveConfigParameterHaCommand>(command => command.Property == 21), cancellation.Token))
                .Returns((GetRawZWaveConfigParameterHaCommand _, CancellationToken token) =>
                {
                    entered.SetResult();
                    return response.Task.WaitAsync(token);
                });
            Task reconciliation = CreateManager(connection).ReconcileDimmersToAsync(
                new LedInputTable().CreateWithColor(0, LedStatusColor.Red), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => reconciliation.WaitAsync(TimeSpan.FromSeconds(5)));

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), cancellation.Token), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task PreCancelledRawReadsNeverReachTheConnection()
        {
            Mock<IHomeAssistantConnection> connection = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetRawZWaveConfigParameterAsync(
                ZWaveTestDevices.CreateDevice(), 21, cancellation.Token));

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task CancellationAfterARawResponseIsNotMisreportedAsAnInvalidResponse()
        {
            Mock<IHomeAssistantConnection> connection = new();
            using CancellationTokenSource cancellation = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), cancellation.Token))
                .Callback(cancellation.Cancel).ReturnsAsync((JsonElement?)null);

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetRawZWaveConfigParameterAsync(
                ZWaveTestDevices.CreateDevice(), 21, cancellation.Token));
        }

        [TestMethod]
        public async Task RejectedBlinkConfigurationLeavesValidSettingsAndDeviceWorkUsable()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Dictionary<int, int> physical = CreatePhysicalParameters();
            Mock<IHomeAssistantConnection> connection = CreatePhysicalConnection(device, physical);
            Config configuration = new();
            DimmerSyncManager manager = CreateManager(connection, configuration);

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => configuration.DimmerLedBlinkFrequency = 256);
            connection.VerifyNoOtherCalls();
            await manager.ReconcileDimmersToAsync(new LedInputTable().CreateWithColor(0, LedStatusColor.Red), CancellationToken.None);

            _ = physical[21].Should().Be(1);
            _ = physical[30].Should().Be(5);
        }

        [TestMethod]
        [DataRow("-1")]
        [DataRow("256")]
        [DataRow("2147483647")]
        public void InvalidBoundConfigurationFailsBeforeAppConstruction(string value)
        {
            IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DimmerLedBlinkFrequency"] = value }).Build();

            Action bind = () => configuration.Get<Config>();
            _ = bind.Should().Throw<TargetInvocationException>().WithInnerException<ArgumentOutOfRangeException>();
        }

        /// <summary>
        /// Supplies physical values independently of the HA cache so stale-cache behavior can be reproduced.
        /// </summary>
        /// <returns>Complete raw parameters for a default dimmer.</returns>
        private static Dictionary<int, int> CreatePhysicalParameters()
        {
            Dictionary<int, int> parameters = new() { [13] = 1, [30] = 5, [31] = 0 };
            for (int i = 0; i < 7; ++i)
            {
                parameters[21 + i] = 0;
            }
            return parameters;
        }

        /// <summary>
        /// Models completed device reads and writes without changing the independently supplied HA cache.
        /// </summary>
        /// <param name="device">Device represented by the connection.</param>
        /// <param name="physical">Mutable physical parameter values.</param>
        /// <returns>A connection whose raw reads expose actual simulated device values.</returns>
        private static Mock<IHomeAssistantConnection> CreatePhysicalConnection(HassDeviceExtended device, Dictionary<int, int> physical)
        {
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<GetRawZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetRawZWaveConfigParameterHaCommand command, CancellationToken _) =>
                    JsonSerializer.SerializeToElement(new { value = physical[command.Property] }));
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(
                It.IsAny<SetZWaveConfigParameterHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SetZWaveConfigParameterHaCommand command, CancellationToken _) =>
                {
                    physical[command.Property] = command.PropertyKey is int mask
                        ? (physical[command.Property] & ~mask) | (command.Value == 0 ? 0 : mask)
                        : command.Value;
                    return JsonSerializer.SerializeToElement(new { status = "accepted" });
                });
            return connection;
        }

        /// <summary>
        /// Creates the real synchronization manager so tests exercise both device decoding and update decisions.
        /// </summary>
        /// <param name="connection">Connection used for cached and physical requests.</param>
        /// <param name="configuration">Valid application settings, or defaults when omitted.</param>
        /// <returns>A manager driven by the test connection and clock.</returns>
        private static DimmerSyncManager CreateManager(Mock<IHomeAssistantConnection> connection, Config? configuration = null)
        {
            Mock<IHomeAssistantRunner> runner = new();
            _ = runner.SetupGet(r => r.CurrentConnection).Returns(connection.Object);
            Mock<IAppConfig<Config>> config = new();
            _ = config.SetupGet(c => c.Value).Returns(configuration ?? new Config());
            return new DimmerSyncManager(NullLogger<DimmerSyncManager>.Instance, config.Object, runner.Object, new HistoricalScheduler());
        }
    }
}