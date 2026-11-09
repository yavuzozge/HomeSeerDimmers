using FluentAssertions;
using Moq;
using NetDaemon.Client;
using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using static Ozy.HomeSeerDimmers.Apps.Dimmers.Commands.CommandConnectionExtensions;

namespace HomeSeerDimmerUnitTests.Commands
{
    /// <summary>
    /// Unit tests for <see cref="CommandConnectionExtensions"/>
    /// </summary>
    [TestClass]
    public class BasicCommandConnectionExtensionsTests
    {
        [TestMethod]
        public async Task NullRegistryResponsesAreNotTreatedAsSuccessfulEmptyDiscovery()
        {
            Mock<IHomeAssistantConnection> connection = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyCollection<HassDeviceExtended>?)null);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Object.GetDevicesExtendedAsync(CancellationToken.None));
        }

        [TestMethod]
        public async Task CancellationAfterDiscoveryIsReportedBeforeUsingTheResponse()
        {
            Mock<IHomeAssistantConnection> connection = new();
            using CancellationTokenSource cancellation = new();
            _ = connection.Setup(c => c.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                It.IsAny<BasicHaCommand>(), cancellation.Token)).Callback(cancellation.Cancel)
                .ReturnsAsync((IReadOnlyCollection<HassDeviceExtended>?)null);

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.Object.GetDevicesExtendedAsync(cancellation.Token));
        }

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

            _ = mockConnection
                .Setup(m => m.SendCommandAndReturnResponseAsync<BasicHaCommand, IReadOnlyCollection<HassDeviceExtended>>(
                    It.Is<BasicHaCommand>(c => string.Equals(c.Type, "config/device_registry/list", StringComparison.Ordinal)),
                    CancellationToken.None))
                .ReturnsAsync(new[] { device });

            // Act
            IEnumerable<HassDeviceExtended> result = await mockConnection.Object.GetDevicesExtendedAsync(CancellationToken.None);

            // Assert
            _ = result.Should().BeEquivalentTo(new[] { device });
        }
    }
}