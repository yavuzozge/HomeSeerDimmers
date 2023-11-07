using Ozy.HomeSeerDimmers.Apps.Dimmers.DimmerDevice;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Extension methods for <see cref="LedInputTable"/>
    /// </summary>
    public static class LedInputTableExtensions
    {
        /// <summary>
        /// Returns a copy of input table that contains the color in the given index
        /// </summary>
        /// <param name="idx">Index</param>
        /// <param name="color">Color</param>
        /// <returns>New instance of <see cref="LedInputTable"/> that contains the change</returns>
        public static LedInputTable CreateWithColor(this LedInputTable table, int idx, LedStatusColor color)
        {
            return new LedInputTable(table.Colors.SetItem(idx, color), table.Blinks);
        }

        /// <summary>
        /// Returns a copy of input table that contains the blink in the given index
        /// </summary>
        /// <param name="idx">Index</param>
        /// <param name="blink">Blink</param>
        /// <returns>New instance of <see cref="LedInputTable"/> that contains the change</returns>
        public static LedInputTable CreateWithBlink(this LedInputTable table, int idx, LedBlink blink)
        {
            return new LedInputTable(table.Colors, table.Blinks.SetItem(idx, blink));
        }
    }
}
