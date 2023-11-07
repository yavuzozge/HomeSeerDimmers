using System;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers.Commands
{
    /// <summary>
    /// Indicates that a device is not a valid Z-Wave device
    /// </summary>
    public class InvalidZWaveDeviceException : InvalidOperationException
    {
        /// <summary>
        /// Helper method ensuring that the device is a Z-Wave device
        /// </summary>
        /// <param name="device"></param>
        /// <exception cref="InvalidZWaveDeviceException">If device is not a Z-Wave device</exception>
        public static void ThrowIfInvalid(HassDeviceExtended device)
        {
            if (!device.TryGetZWaveNodeId(out int _))
            {
                throw new InvalidZWaveDeviceException();
            }
        }
    }
}
