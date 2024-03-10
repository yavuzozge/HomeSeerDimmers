using System.Collections.Immutable;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice
{
    /// <summary>
    /// HomeSeer dimmer configuration
    /// </summary>
    public record DimmerConfig(
        ImmutableArray<LedStatusColor> Colors,
        ImmutableArray<LedBlink> Blinks,
        CustomLedStatusMode CustomLedStatusMode,
        int BlinkFrequency);

}
