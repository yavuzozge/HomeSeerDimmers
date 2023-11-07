using FluentAssertions;
using Moq;
using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using static Ozy.HomeSeerDimmers.Apps.Dimmers.Commands.CommandConnectionExtensions;

namespace HomeSeerDimmerUnitTests
{
    [TestClass]
    public class BasicCommandConnectionExtensionsTests
    {
        [TestMethod]
        public async Task GetDevicesExtendedAsyncCallsConnectionSuccesfully()
        {
            // Arrange
            Mock<IHomeAssistantConnection> mockConnection = new();
            HassDeviceExtended device = new(
                "Id",
                "AreaId",
                "Name",
                "NameByUser",
                "Manufacturer",
                "Model",
                new List<IList<string>>
                {
                    new List<string>
                    {
                        "Identifier1", "Identifier2"
                    }
            });

            mockConnection
                .Setup(m => m.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                    It.Is<BasicHaCommand>(c => string.Equals(c.Type, "config/device_registry/list", StringComparison.Ordinal)),
                    CancellationToken.None))
                .ReturnsAsync(new[] { device });

            // Act
            IEnumerable<HassDeviceExtended> result = await mockConnection.Object.GetDevicesExtendedAsync(CancellationToken.None);

            // Assert
            result.Should().BeEquivalentTo(new[] { device });
        }
    }
}
