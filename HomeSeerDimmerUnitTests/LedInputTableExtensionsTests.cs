using FluentAssertions;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Unit tests for <see cref="LedInputTableExtensions"/>
    /// </summary>
    [TestClass]
    public class LedInputTableExtensionsTests
    {
        [TestMethod]
        public void CreateWithColorCreatesANewTableAndUpdatesTheValue()
        {
            // Arrange
            LedInputTable table = new();

            // Act
            LedInputTable table2 = table.CreateWithColor(1, LedStatusColor.Blue);

            // Assery
            table.Should().NotBeSameAs(table2);
            table.Colors[1].Should().Be(LedStatusColor.Off);
            table2.Colors[1].Should().Be(LedStatusColor.Blue);
        }

        [TestMethod]
        public void CreateWithBlinkCreatesANewTableAndUpdatesTheValue()
        {
            // Arrange
            LedInputTable table = new();

            // Act
            LedInputTable table2 = table.CreateWithBlink(1, LedBlink.On);

            // Assery
            table.Should().NotBeSameAs(table2);
            table.Blinks[1].Should().Be(LedBlink.Off);
            table2.Blinks[1].Should().Be(LedBlink.On);
        }
    }
}
