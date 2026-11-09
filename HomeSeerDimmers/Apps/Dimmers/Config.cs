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
        /// The registry name or user-assigned Home Assistant name of the device to refresh.
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
        /// Holds a valid blink interval so malformed configuration cannot reach device operations.
        /// </summary>
        private int dimmerLedBlinkFrequency = 5;

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
        /// Blink interval in units of 100 milliseconds, from 0 through 255; zero disables blinking.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The configured interval is outside the supported range.</exception>
        public int DimmerLedBlinkFrequency
        {
            get => this.dimmerLedBlinkFrequency;
            set
            {
                if (value is < 0 or > byte.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(DimmerLedBlinkFrequency), value,
                        "The LED blink interval must be from 0 through 255, in units of 100 milliseconds.");
                }
                this.dimmerLedBlinkFrequency = value;
            }
        }

        /// <summary>
        /// How long LED synchronization and ping operations reuse their device discovery.
        /// Zero means rediscovery with every synchronization or ping operation.
        /// </summary>
        public TimeSpan ZWaveDevicesDiscoveryValidity { get; set; }

        /// <summary>
        /// Interval for LED sync (set zero to disable)
        /// </summary>
        public TimeSpan LedSyncInterval { get; set; }

        /// <summary>
        /// Interval for Z-Wave device ping (set zero to disable)
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