using Moq;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System.Text.Json;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Supplies valid registry entries and configuration responses for Z-Wave tests.
    /// </summary>
    internal static class ZWaveTestDevices
    {
        internal static HassDeviceExtended CreateDevice(string id = "device", string name = "Dimmer", int nodeId = 37)
        {
            return new HassDeviceExtended(id, "area", name, "", "HomeSeer Technologies", "HS-WX300",
                new List<IList<string>> { new List<string> { "zwave_js", $"4023755774-{nodeId}" } });
        }

        internal static Dictionary<string, ZwaveConfigParameters> CreateConfiguration(HassDeviceExtended device)
        {
            int nodeId = device.GetZWaveNodeId();
            Dictionary<string, ZwaveConfigParameters> result = [];
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                result[$"{nodeId}-112-0-{21 + i}"] = new ZwaveConfigParameters(21 + i, null, "enumerated", JsonSerializer.SerializeToElement(0));
                result[$"{nodeId}-112-0-31-{1 << i}"] = new ZwaveConfigParameters(31, 1 << i, "enumerated", JsonSerializer.SerializeToElement(0));
            }
            result[$"{nodeId}-112-0-13"] = new ZwaveConfigParameters(13, null, "enumerated", JsonSerializer.SerializeToElement(1));
            result[$"{nodeId}-112-0-30"] = new ZwaveConfigParameters(30, null, "manual_entry", JsonSerializer.SerializeToElement(5));
            return result;
        }

        internal static Mock<IHomeAssistantConnection> CreateConnection(params HassDeviceExtended[] devices)
        {
            Mock<IHomeAssistantConnection> connection = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(devices);
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<GetZWaveConfigParametersHaCommand, IDictionary<string, ZwaveConfigParameters>>(
                It.IsAny<GetZWaveConfigParametersHaCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetZWaveConfigParametersHaCommand command, CancellationToken _) =>
                    CreateConfiguration(devices.Single(device => device.Id == command.DeviceId)));
            _ = connection.Setup(c => c.SendCommandAndReturnResponseRawAsync(It.IsAny<CommandMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToElement(new { value_id = "value", status = "accepted" }));
            return connection;
        }
    }
}