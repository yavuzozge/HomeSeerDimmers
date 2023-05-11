using System;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Monitors state changes of "input" entitites (the entities that are used in Home Assistant 
    /// to represent what a LED color and blink should be)
    /// </summary>
    public interface ILedInputMonitor
    {
        /// <summary>
        /// Observer that gets updated with the data from the monitored entities for LED inputs
        /// </summary>
        IObservable<LedInputTable> AllInputTableChanges { get; }
    }
}