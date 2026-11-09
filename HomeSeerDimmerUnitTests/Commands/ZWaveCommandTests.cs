using FluentAssertions;
using Moq;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Collections.Immutable;
using System.Text.Json;

namespace HomeSeerDimmerUnitTests.Commands
{
    /// <summary>
    /// Covers configuration payloads, device identifiers, and response parsing.
    /// </summary>
    [TestClass]
    public class ZWaveCommandTests
    {
        [TestMethod]
        [DataRow(0)]
        [DataRow(10)]
        [DataRow(255)]
        public async Task BlinkFrequencyIsSerializedAsTheRequestedJsonInteger(int frequency)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            using CancellationTokenSource cancellation = new();

            await connection.Object.SetZWaveBlinkFrequencyAsync(device, frequency, cancellation.Token);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<CommandMessage>(command => IsParameter(command, device.Id, 30, null, frequency)),
                cancellation.Token), Times.Once);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        public async Task LedReadsAndWritesUseTheCorrectColorParameterAndBlinkMask(int index)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            configuration[$"37-112-0-{21 + index}"] = configuration[$"37-112-0-{21 + index}"] with
            {
                Value = JsonSerializer.SerializeToElement(7)
            };
            configuration[$"37-112-0-31-{1 << index}"] = configuration[$"37-112-0-31-{1 << index}"] with
            {
                Value = JsonSerializer.SerializeToElement(1)
            };
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), CancellationToken.None)).ReturnsAsync(configuration);

            DimmerConfig current = await connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None);

            _ = current.Colors.Should().Equal(Enumerable.Range(0, 7).Select(i => i == index ? LedStatusColor.White : LedStatusColor.Off));
            _ = current.Blinks.Should().Equal(Enumerable.Range(0, 7).Select(i => i == index ? LedBlink.On : LedBlink.Off));

            await connection.Object.SetZWaveLedColorAsync(device, index, LedStatusColor.White, CancellationToken.None);
            await connection.Object.SetZWaveLedBlinkAsync(device, index, LedBlink.On, CancellationToken.None);
            await connection.Object.SetZWaveLedBlinkAsync(device, index, LedBlink.Off, CancellationToken.None);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<CommandMessage>(command => IsParameter(command, device.Id, 21 + index, null, 7)), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<CommandMessage>(command => IsParameter(command, device.Id, 31, 1 << index, 1)), CancellationToken.None), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<CommandMessage>(command => IsParameter(command, device.Id, 31, 1 << index, 0)), CancellationToken.None), Times.Once);
        }

        [TestMethod]
        [DataRow(-1)]
        [DataRow(7)]
        [DataRow(31)]
        [DataRow(32)]
        [DataRow(int.MinValue)]
        [DataRow(int.MaxValue)]
        public void InvalidLedPositionsAreRejectedBeforeParameterArithmetic(int index)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = new();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveLedColorAsync(device, index, LedStatusColor.Red, CancellationToken.None)).ParamName.Should().Be("index");
            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveLedBlinkAsync(device, index, LedBlink.On, CancellationToken.None)).ParamName.Should().Be("index");

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        [DataRow(-1, -1, -1)]
        [DataRow(8, 2, 2)]
        [DataRow(int.MaxValue, int.MaxValue, int.MaxValue)]
        public void UndefinedLedEnumsAreRejectedBeforeSendingCommands(int colorValue, int blinkValue, int modeValue)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = new();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveLedColorAsync(device, 0, (LedStatusColor)colorValue, CancellationToken.None)).ParamName.Should().Be("color");
            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveLedBlinkAsync(device, 0, (LedBlink)blinkValue, CancellationToken.None)).ParamName.Should().Be("blink");
            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveCustomLedStatusModeAsync(device, (CustomLedStatusMode)modeValue, CancellationToken.None)).ParamName.Should().Be("mode");

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        [DataRow(-1)]
        [DataRow(256)]
        [DataRow(int.MaxValue)]
        public void OutOfRangeBlinkFrequenciesAreRejectedBeforeSendingCommands(int frequency)
        {
            Mock<IHomeAssistantConnection> connection = new();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
                connection.Object.SetZWaveBlinkFrequencyAsync(ZWaveTestDevices.CreateDevice(), frequency, CancellationToken.None))
                .ParamName.Should().Be("blinkFrequency");

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        [DataRow(CustomLedStatusMode.Disable)]
        [DataRow(CustomLedStatusMode.Enable)]
        public async Task BothCustomLedModesAreSerializedAsJsonIntegers(CustomLedStatusMode mode)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            using CancellationTokenSource cancellation = new();

            await connection.Object.SetZWaveCustomLedStatusModeAsync(device, mode, cancellation.Token);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<CommandMessage>(command => IsParameter(command, device.Id, 13, null, (int)mode)),
                cancellation.Token), Times.Once);
        }

        [TestMethod]
        [DataRow("{\"status\":\"rejected\"}")]
        [DataRow("{\"status\":\"working\"}")]
        [DataRow("{\"status\":null}")]
        [DataRow("{}")]
        [DataRow("\"invalid response\"")]
        [DataRow("{\"status\":1}")]
        public async Task UnsuccessfulOrMalformedSetResponsesAreReported(string json)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<CommandMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.Deserialize<JsonElement>(json));

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.Object.SetZWaveConfigParameterAsync(device, 30, 10, CancellationToken.None));
        }

        [TestMethod]
        public async Task NullSetResponseIsReported()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<CommandMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((JsonElement?)null);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.Object.SetZWaveCustomLedStatusModeAsync(device, CustomLedStatusMode.Enable, CancellationToken.None));
        }

        [TestMethod]
        public async Task NullConfigurationResponsesAreReported()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IDictionary<string, ZwaveConfigParameters>?)null);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Object.GetZWaveConfigParametersAsync(device, CancellationToken.None));
        }

        [TestMethod]
        public async Task MissingOrUndefinedLedConfigurationIsRejected()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            configuration["37-112-0-27"] = new ZwaveConfigParameters(27, null, "enumerated", JsonSerializer.SerializeToElement(8));
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None));
            _ = configuration.Remove("37-112-0-27");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("37-112-0-31-64")]
        [DataRow("37-112-0-13")]
        [DataRow("37-112-0-30")]
        public async Task MissingBlinkModeOrFrequencyConfigurationIsRejected(string key)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            _ = configuration.Remove(key);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("37-112-0-31-64", 2)]
        [DataRow("37-112-0-13", 2)]
        [DataRow("37-112-0-30", -1)]
        [DataRow("37-112-0-30", 256)]
        public async Task OutOfRangeDeviceConfigurationIsRejected(string key, int value)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            Dictionary<string, ZwaveConfigParameters> configuration = ZWaveTestDevices.CreateConfiguration(device);
            configuration[key] = configuration[key] with { Value = JsonSerializer.SerializeToElement(value) };
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None));
        }

        [TestMethod]
        public async Task RefreshCommandsUseTheDeviceIdAndForwardCancellation()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            using CancellationTokenSource cancellation = new();

            await connection.Object.RefreshNodeValuesAsync(device, cancellation.Token);
            await connection.Object.RefreshNodeCcValuesAsync(device, ZWaveCommandClassId.DoorLock, cancellation.Token);

            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeValuesHaCommand>(command => command.Type == "zwave_js/refresh_node_values"
                    && command.DeviceId == device.Id), cancellation.Token), Times.Once);
            connection.Verify(c => c.SendCommandAndReturnResponseRawAsync(
                It.Is<RefreshNodeCcValuesHaCommand>(command => command.Type == "zwave_js/refresh_node_cc_values"
                    && command.DeviceId == device.Id && command.CommandClassId == (int)ZWaveCommandClassId.DoorLock), cancellation.Token), Times.Once);
        }

        [TestMethod]
        public async Task PreCancelledCommandsDoNotAccessHomeAssistant()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetDevicesExtendedAsync(cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetZWaveConfigParametersAsync(device, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveConfigParameterAsync(device, 30, 10, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveConfigParameterAsync(device, 31, 64, 1, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetZWaveDimmerConfigAsync(device, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveLedColorAsync(device, 0, LedStatusColor.Red, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveLedBlinkAsync(device, 0, LedBlink.On, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveCustomLedStatusModeAsync(device, CustomLedStatusMode.Enable, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveBlinkFrequencyAsync(device, 10, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.RefreshNodeValuesAsync(device, cancellation.Token));
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.RefreshNodeCcValuesAsync(device, ZWaveCommandClassId.DoorLock, cancellation.Token));

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task CancellationAfterAResponsePreventsFurtherProcessing()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice();
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            using CancellationTokenSource cancellation = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<SetZWaveConfigParameterHaCommand>(), cancellation.Token))
                .Callback(cancellation.Cancel).ReturnsAsync((JsonElement?)null);

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.SetZWaveConfigParameterAsync(device, 30, 10, cancellation.Token));
        }

        [TestMethod]
        [DataRow("[[\"other\",\"id\"],[\"zwave_js\",\"4023755774-37\"]]", true, 37)]
        [DataRow("[[\"zwave_js\",\"invalid\"],[\"zwave_js\",\"4023755774-37\"]]", true, 37)]
        [DataRow("[[],[\"zwave_js\",\"4023755774-37-12:17479:12342\"]]", true, 37)]
        [DataRow("[null,[\"zwave_js\",\"4023755774-37\"]]", true, 37)]
        [DataRow("[[\"zwave_js\",null],[\"zwave_js\",\"4023755774-37\"]]", true, 37)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-1\"]]", true, 37)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-g2\"]]", true, 37)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-bad\"],[\"zwave_js\",\"4023755774-37\"]]", true, 37)]
        [DataRow("[]", false, 0)]
        [DataRow("null", false, 0)]
        [DataRow("[[\"other\",\"4023755774-37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-0\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774--1\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-2147483648\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"invalid-37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"-37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"0-37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4294967296-37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37 \"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-+37\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37\",\"extra\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-bad\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-1-extra\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-12:17479\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-12:17479:12342:1\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-12:17479:65536\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-g\"]]", false, 0)]
        [DataRow("[[\"zwave_js\",\"4023755774-37-g0\"]]", false, 0)]
        public void NodeIdentificationDoesNotDependOnIdentifierOrder(string json, bool expected, int expectedNodeId)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice() with
            {
                Identifiers = JsonSerializer.Deserialize<IList<IList<string>>>(json)!
            };

            _ = device.TryGetZWaveNodeId(out int nodeId).Should().Be(expected);
            _ = nodeId.Should().Be(expectedNodeId);
        }

        [TestMethod]
        public void NullDevicesDoNotHaveAZWaveNodeId()
        {
            _ = ((HassDeviceExtended)null!).TryGetZWaveNodeId(out int nodeId).Should().BeFalse();
            _ = nodeId.Should().Be(0);
            _ = Assert.Throws<InvalidZWaveDeviceException>(() => ((HassDeviceExtended)null!).GetZWaveNodeId());
        }

        [TestMethod]
        public void HomeAssistantJsonIsParsedUsingTheExternalRegistryAndParameterFieldNames()
        {
            const string registryJson = """
                {
                  "id": "device-38",
                  "area_id": "living_room",
                  "name": "Hall dimmer",
                  "name_by_user": "Hallway",
                  "manufacturer": "HomeSeer Technologies",
                  "model": "HS-WD200+",
                  "disabled_by": "user",
                  "identifiers": [["other", "bridge"], ["zwave_js", "4023755774-38"]]
                }
                """;
            HassDeviceExtended device = JsonSerializer.Deserialize<HassDeviceExtended>(registryJson)!;
            _ = device.Should().BeEquivalentTo(new HassDeviceExtended(
                "device-38", "living_room", "Hall dimmer", "Hallway", "HomeSeer Technologies", "HS-WD200+",
                new List<IList<string>> { new List<string> { "other", "bridge" }, new List<string> { "zwave_js", "4023755774-38" } })
            {
                DisabledBy = "user"
            });
            _ = device.GetZWaveNodeId().Should().Be(38);

            const string parameterJson = """
                {"property":31,"property_key":64,"configuration_value_type":"enumerated","value":1}
                """;
            ZwaveConfigParameters parameter = JsonSerializer.Deserialize<ZwaveConfigParameters>(parameterJson)!;

            _ = parameter.Property.Should().Be(31);
            _ = parameter.PropertyKey.Should().Be(64);
            _ = parameter.TryGetEnumeratedValue(out LedBlink blink).Should().BeTrue();
            _ = blink.Should().Be(LedBlink.On);
        }

        [TestMethod]
        [DataRow("HS-WD200+")]
        [DataRow("HS-WX300")]
        public async Task ConfigurationResponsePreservesEveryLedPositionAndDeviceSetting(string model)
        {
            const string json = """
                {
                  "38-112-0-21": {"property":21,"property_key":null,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-22": {"property":22,"property_key":null,"configuration_value_type":"enumerated","value":2},
                  "38-112-0-23": {"property":23,"property_key":null,"configuration_value_type":"enumerated","value":3},
                  "38-112-0-24": {"property":24,"property_key":null,"configuration_value_type":"enumerated","value":4},
                  "38-112-0-25": {"property":25,"property_key":null,"configuration_value_type":"enumerated","value":5},
                  "38-112-0-26": {"property":26,"property_key":null,"configuration_value_type":"enumerated","value":6},
                  "38-112-0-27": {"property":27,"property_key":null,"configuration_value_type":"enumerated","value":7},
                  "38-112-0-31-1": {"property":31,"property_key":1,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-31-2": {"property":31,"property_key":2,"configuration_value_type":"enumerated","value":0},
                  "38-112-0-31-4": {"property":31,"property_key":4,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-31-8": {"property":31,"property_key":8,"configuration_value_type":"enumerated","value":0},
                  "38-112-0-31-16": {"property":31,"property_key":16,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-31-32": {"property":31,"property_key":32,"configuration_value_type":"enumerated","value":0},
                  "38-112-0-31-64": {"property":31,"property_key":64,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-13": {"property":13,"property_key":null,"configuration_value_type":"enumerated","value":1},
                  "38-112-0-30": {"property":30,"property_key":null,"configuration_value_type":"manual_entry","value":10}
                }
                """;
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice("secondary-device", nodeId: 38) with { Model = model };
            Mock<IHomeAssistantConnection> connection = ZWaveTestDevices.CreateConnection(device);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), CancellationToken.None))
                .ReturnsAsync(JsonSerializer.Deserialize<Dictionary<string, ZwaveConfigParameters>>(json));

            DimmerConfig result = await connection.Object.GetZWaveDimmerConfigAsync(device, CancellationToken.None);

            _ = result.Should().BeEquivalentTo(new DimmerConfig(
                ImmutableArray.Create(LedStatusColor.Red, LedStatusColor.Green, LedStatusColor.Blue, LedStatusColor.Magenta,
                    LedStatusColor.Yellow, LedStatusColor.Cyan, LedStatusColor.White),
                ImmutableArray.Create(LedBlink.On, LedBlink.Off, LedBlink.On, LedBlink.Off, LedBlink.On, LedBlink.Off, LedBlink.On),
                CustomLedStatusMode.Enable, 10), options => options.WithStrictOrdering());
            connection.Verify(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.Is<GetZWaveConfigParametersHaCommand>(command => command.DeviceId == device.Id), CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task InvalidDevicesAreRejectedBeforeSendingCommands()
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice() with { Identifiers = new List<IList<string>>() };
            Mock<IHomeAssistantConnection> connection = new();

            _ = await Assert.ThrowsAsync<InvalidZWaveDeviceException>(() =>
                connection.Object.SetZWaveConfigParameterAsync(device, 30, 10, CancellationToken.None));
            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" ")]
        public async Task MissingRegistryIdsAreRejectedBeforeSendingCommands(string? id)
        {
            HassDeviceExtended device = ZWaveTestDevices.CreateDevice() with { Id = id! };
            Mock<IHomeAssistantConnection> connection = new();

            _ = await Assert.ThrowsAsync<InvalidZWaveDeviceException>(() =>
                connection.Object.GetZWaveConfigParametersAsync(device, CancellationToken.None));
            _ = await Assert.ThrowsAsync<InvalidZWaveDeviceException>(() =>
                connection.Object.SetZWaveConfigParameterAsync(device, 30, 10, CancellationToken.None));
            _ = await Assert.ThrowsAsync<InvalidZWaveDeviceException>(() =>
                connection.Object.RefreshNodeValuesAsync(device, CancellationToken.None));
            _ = await Assert.ThrowsAsync<InvalidZWaveDeviceException>(() =>
                connection.Object.RefreshNodeCcValuesAsync(device, ZWaveCommandClassId.Basic, CancellationToken.None));

            connection.VerifyNoOtherCalls();
        }

        [TestMethod]
        [DataRow("0", true, LedStatusColor.Off)]
        [DataRow("7", true, LedStatusColor.White)]
        [DataRow("8", false, LedStatusColor.Off)]
        [DataRow("-1", false, LedStatusColor.Off)]
        [DataRow("\"1\"", false, LedStatusColor.Off)]
        [DataRow("1.5", false, LedStatusColor.Off)]
        [DataRow("2147483648", false, LedStatusColor.Off)]
        [DataRow("null", false, LedStatusColor.Off)]
        [DataRow("{}", false, LedStatusColor.Off)]
        public void EnumeratedValuesRequireADefinedInteger(string json, bool expected, LedStatusColor expectedColor)
        {
            ZwaveConfigParameters configuration = new(21, null, "enumerated", JsonSerializer.Deserialize<JsonElement>(json));

            _ = configuration.TryGetEnumeratedValue(out LedStatusColor color).Should().Be(expected);
            _ = color.Should().Be(expectedColor);
        }

        [TestMethod]
        [DataRow("10", true, 10)]
        [DataRow("255", true, 255)]
        [DataRow("\"10\"", false, 0)]
        [DataRow("1.5", false, 0)]
        [DataRow("2147483648", false, 0)]
        [DataRow("null", false, 0)]
        public void ManualValuesRequireAJsonInteger(string json, bool expected, int expectedValue)
        {
            ZwaveConfigParameters configuration = new(30, null, "manual_entry", JsonSerializer.Deserialize<JsonElement>(json));

            _ = configuration.TryGetIntValue(out int value).Should().Be(expected);
            _ = value.Should().Be(expectedValue);
        }

        [TestMethod]
        public void MissingOrMismatchedConfigurationTypesAreNotAccepted()
        {
            ZwaveConfigParameters configuration = new(21, null, "manual_entry", JsonSerializer.SerializeToElement(1));
            _ = configuration.TryGetEnumeratedValue(out LedStatusColor _).Should().BeFalse();
            _ = (configuration with { ValueType = "enumerated" }).TryGetIntValue(out _).Should().BeFalse();
            _ = ((ZwaveConfigParameters)null!).TryGetEnumeratedValue(out LedStatusColor _).Should().BeFalse();
            _ = ((ZwaveConfigParameters)null!).TryGetIntValue(out _).Should().BeFalse();
        }

        [TestMethod]
        [DataRow(int.MinValue, true, int.MinValue)]
        [DataRow(int.MaxValue, true, int.MaxValue)]
        [DataRow(1, false, 0)]
        public void NumericEnumParsingPreservesTheFullSignedIntegerRange(int value, bool expected, int expectedValue)
        {
            ZwaveConfigParameters configuration = new(21, null, "enumerated", JsonSerializer.SerializeToElement(value));

            _ = configuration.TryGetEnumeratedValue(out SignedTestValue parsed).Should().Be(expected);
            _ = parsed.Should().Be((SignedTestValue)expectedValue);
        }

        [TestMethod]
        [DataRow(0, true, 0)]
        [DataRow(255, true, 255)]
        [DataRow(256, false, 0)]
        [DataRow(-1, false, 0)]
        public void NumericEnumParsingDoesNotWrapNarrowUnderlyingTypes(int value, bool expected, int expectedValue)
        {
            ZwaveConfigParameters configuration = new(21, null, "enumerated", JsonSerializer.SerializeToElement(value));

            _ = configuration.TryGetEnumeratedValue(out ByteTestValue parsed).Should().Be(expected);
            _ = parsed.Should().Be((ByteTestValue)expectedValue);
        }

        /// <summary>
        /// Supplies signed boundary values for enum parsing tests.
        /// </summary>
        private enum SignedTestValue
        {
            Minimum = int.MinValue,
            None = 0,
            Maximum = int.MaxValue
        }

        /// <summary>
        /// Supplies byte boundary values for enum parsing tests.
        /// </summary>
        private enum ByteTestValue : byte
        {
            None = 0,
            Maximum = byte.MaxValue
        }

        /// <summary>
        /// Checks the serialized parameter request so tests catch wrong addresses and numbers sent as strings.
        /// </summary>
        /// <param name="command">Outgoing command to inspect.</param>
        /// <param name="deviceId">Expected Home Assistant device identifier.</param>
        /// <param name="property">Expected configuration parameter number.</param>
        /// <param name="propertyKey">Expected bit mask, or null for a complete parameter.</param>
        /// <param name="value">Expected integer value.</param>
        /// <returns>Whether the request contains the expected address and numeric value.</returns>
        private static bool IsParameter(CommandMessage command, string deviceId, int property, int? propertyKey, int value)
        {
            JsonElement json = JsonSerializer.SerializeToElement(command, command.GetType());
            return command.Type == "zwave_js/set_config_parameter"
                && json.GetProperty("device_id").GetString() == deviceId
                && json.GetProperty("property").GetInt32() == property
                && (propertyKey == null ? !json.TryGetProperty("property_key", out _)
                    : json.GetProperty("property_key").GetInt32() == propertyKey)
                && json.GetProperty("value").ValueKind == JsonValueKind.Number
                && json.GetProperty("value").GetInt32() == value;
        }
    }
}