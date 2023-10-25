using System.Collections.Immutable;
using System.Linq;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Contains the latest state of LED inputs as represented in Home Assistant: 
    /// the states are read from the entities in Home Assistant that represents what a LED color and blink should be
    /// </summary>
    /// <param name="Colors">A container of colors that the LEDs should have. There would be items from 0..6 (6 being the top LED)</param>
    /// <param name="Blinks">A container of blink states that the LEDs should have. There would be items from 0..6 (6 being the top LED)</param>
    public record LedInputTable(ImmutableArray<LedStatusColor> Colors, ImmutableArray<LedBlink> Blinks)
    {
        /// <summary>
        /// Number of leds in a HomeSeer dimmer device
        /// </summary>
        public const int NumberOfLeds = 7;

        /// <summary>
        /// Factory method that creates a LED input table with default values
        /// </summary>
        /// <returns>New instance of <see cref="LedInputTable"/></returns>
        public LedInputTable()
            : this(Enumerable.Repeat(LedStatusColor.Off, NumberOfLeds).ToImmutableArray(),
                  Enumerable.Repeat(LedBlink.Off, NumberOfLeds).ToImmutableArray())
        {
        }
    }
}
