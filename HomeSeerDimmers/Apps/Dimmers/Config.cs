using Ozy.HomeSeerDimmers.Apps.Dimmers.Commands;
using System;
using System.Collections.Generic;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Configures Z-Wave device for ping
    /// </summary>
    public class PingZwaveDevice
    {
        /// <summary>
        /// The name of the device
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The particular command class to refresh the node values for
        /// </summary>
        public ZWaveCommandClassId RefreshCommandClassId { get; set; } = ZWaveCommandClassId.NoOperation;
    }

    /// <summary>
    /// Application configuration
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Pattern to match with Home Assistant entity names what we will monitor 
        /// to get the dimmer LED color state changes. {0} will be from 1-7
        /// e.g.: sensor.dimmer_led_{0}_color
        /// </summary>
        public string DimmerLedColorEntityNamePattern { get; set; } = string.Empty;

        /// <summary>
        /// Pattern to match with Home Assistant entity names what we will monitor 
        /// to get the dimmer LED blink state changes. {0} will be from 1-7
        /// e.g.: binary_sensor.dimmer_led_{0}_blink
        /// </summary>
        public string DimmerLedBlinkEntityNamePattern { get; set; } = string.Empty;

        /// <summary>
        /// LED sync interval (set zero to disable)
        /// </summary>
        public TimeSpan LedSyncInterval { get; set; }

        /// <summary>
        /// Z-Wave device ping interval (set zero to disable)
        /// Used with <see cref="ZWavePingDevices"/>
        /// </summary>
        public TimeSpan ZWavePingInterval { get; set; }

        /// <summary>
        /// Collection of Z-Wave devices to ping
        /// Note that <see cref="ZWavePingInterval"/> would need to be set to a non-zero value
        /// </summary>
        public IList<PingZwaveDevice> ZWavePingDevices { get; set; } = new List<PingZwaveDevice>();
    }
}
