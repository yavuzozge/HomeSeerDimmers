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
            _ = table.Should().NotBeSameAs(table2);
            _ = table.Colors[1].Should().Be(LedStatusColor.Off);
            _ = table2.Colors.Should().Equal(LedStatusColor.Off, LedStatusColor.Blue, LedStatusColor.Off,
                LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off);
            _ = table2.Blinks.Equals(table.Blinks).Should().BeTrue();
        }

        [TestMethod]
        public void CreateWithBlinkCreatesANewTableAndUpdatesTheValue()
        {
            // Arrange
            LedInputTable table = new();

            // Act
            LedInputTable table2 = table.CreateWithBlink(1, LedBlink.On);

            // Assery
            _ = table.Should().NotBeSameAs(table2);
            _ = table.Blinks[1].Should().Be(LedBlink.Off);
            _ = table2.Blinks.Should().Equal(LedBlink.Off, LedBlink.On, LedBlink.Off,
                LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off);
            _ = table2.Colors.Equals(table.Colors).Should().BeTrue();
        }

        [TestMethod]
        public void UnchangedColorsAndBlinksReuseTheExistingTable()
        {
            LedInputTable table = new();
            LedInputTable active = table.CreateWithColor(0, LedStatusColor.Red).CreateWithBlink(6, LedBlink.On);

            _ = table.CreateWithColor(0, LedStatusColor.Off).Should().BeSameAs(table);
            _ = table.CreateWithBlink(6, LedBlink.Off).Should().BeSameAs(table);
            _ = active.CreateWithColor(0, LedStatusColor.Red).Should().BeSameAs(active);
            _ = active.CreateWithBlink(6, LedBlink.On).Should().BeSameAs(active);
        }

        [TestMethod]
        [DataRow(-1)]
        [DataRow(LedInputTable.NumberOfLeds)]
        public void InvalidIndicesAreRejectedEvenWhenTheValueIsUnchanged(int index)
        {
            LedInputTable table = new();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.CreateWithColor(index, LedStatusColor.Off));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.CreateWithBlink(index, LedBlink.Off));
        }
    }
}