using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Extension methods for <see cref="LedInputTable"/>
    /// </summary>
    public static class LedInputTableExtensions
    {
        /// <summary>
        /// Returns a table with the color in the given index, reusing unchanged tables
        /// </summary>
        /// <param name="idx">Index</param>
        /// <param name="color">Color</param>
        /// <returns>The original table when unchanged, otherwise an updated copy</returns>
        public static LedInputTable CreateWithColor(this LedInputTable table, int idx, LedStatusColor color)
        {
            return idx >= 0 && idx < table.Colors.Length && table.Colors[idx] == color
                ? table
                : new LedInputTable(table.Colors.SetItem(idx, color), table.Blinks);
        }

        /// <summary>
        /// Returns a table with the blink in the given index, reusing unchanged tables
        /// </summary>
        /// <param name="idx">Index</param>
        /// <param name="blink">Blink</param>
        /// <returns>The original table when unchanged, otherwise an updated copy</returns>
        public static LedInputTable CreateWithBlink(this LedInputTable table, int idx, LedBlink blink)
        {
            return idx >= 0 && idx < table.Blinks.Length && table.Blinks[idx] == blink
                ? table
                : new LedInputTable(table.Colors, table.Blinks.SetItem(idx, blink));
        }
    }
}