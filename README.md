# Managing HomeSeer's Dimmer LEDs (HS-WD200+ and HS-WX300) Reliably in Home Assistant

[![.NET](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/dotnet.yml/badge.svg)](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/github-code-scanning/codeql/badge.svg)](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/github-code-scanning/codeql)

## The problem

I tried to use the LEDs of HomeSeer dimmers in Home Assistant to indicate when my doors were unlocked or left open, but it only worked intermittently. After some experimentation, I identified two main issues:
1. Z-Wave messages configuring the LEDs were sometimes lost/dropped, and the Z-Wave system did not retry sufficiently.
2. Notifications from Z-Wave source devices, such as door locks and sensors, were sometimes lost/dropped as well.

Furthermore, managing LEDs in Home Assistant through Z-Wave set config messages was not intuitive. I wanted to have a more natural approach by using sensors to represent the color and blivk status, such as `sensor.dimmer_led_0_color` and `binary_sensor.dimmer_led_0_blink`. Then these sensors could be modified based on some condition using templated sensors. For instance, the color of the LED could be easily set when a door is opened or based on the outdoor temperature.

## How it works

The project addresses the concerns mentioned above in the following ways:
1. It automatically discovers all HomeSeer dimmers at launch for easy configuration.
2. It monitors Home Assistant entities (such as sensors), representing LED colors and blink statuses for state changes and then translates that to Z-Wave set config messages for the discovered dimmers.
3. It performs a full sync of LEDs in all dimmers periodically by reading the LED color/blink from the Z-Wave device and setting them if they differ from the monitored sensors.
4. It retries failed Z-Wave set config messages by checking the result status, adding additional reliability since these messages are typically treated as fire-and-forget in HA.
5. It supports periodic pings for any Z-Wave device to refresh its state in HA if messages are sometimes dropped. For example, a Z-Wave door lock that sends status updates when locked/unlocked can have its dimmer LEDs change color accordingly. If the message from the door is dropped, pinging the device forces a refresh of its state in HA, keeping the LEDs consistent with the device state.

## Project details

Currently HS-WD200+ and HS-WX300 are supported, though HS-WX300 is not tested.

This project is created in C# and .NET 7 using [NetDaemon](https://netdaemon.xyz/) which would need to be instaled on your HA instance.

## Getting started

1. Install NetDaemon and configure it -- see their website for details [NetDaemon](https://netdaemon.xyz/). I use it as an HA add-on.
2. Make sure that the HomeSeer dimmers are set to "status" mode. Pls refer to the dimmer docs  I recommend setting blink frequency to 500ms. [TBD -- include this to the project]
3. Deploy the release binaries to NetDaemon directory. You can find the latest release [here](https://github.com/yavuzozge/HomeSeerDimmers/releases) or compile it yourself.
4. Modify `appsettings.json` of NetDaemon -- mine looks smt like following:
```JSON
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Warning"
    }
  },
  "NetDaemon": {
    "ApplicationConfigurationFolder": "./Apps"
  },
}
```
5. Define your sensors in HA -- one sensor for each LED color and blink status. Below are some examples using templated enitites (in `template.yaml`):
```YAML
- sensor:
    - name: "Dimmer LED 7 Color"
      state: >
        {% [some condition]  %}
          Red
        {% [some other condition] %}
          Magenta
        {% else %}
          Green
        {% endif %}
    ...
    - name: "Dimmer LED 1 Color"
    ...
- binary_sensor:
    - name: "Dimmer LED 7 Blink"
      state: >-
        {{ [some condition] }}
    ...
    - name: "Dimmer LED 1 Blink"
    ...
```
To see supported colors, pls refer to [LedStatusColor](HomeSeerDimmers/Apps/Dimmers/HomeSeerDevice/LedStatusColor.cs) enum.

6. Create 'settings.yaml' in the Apps/Dimmers directory, and set the configuration of the app to something like:
```YAML
Ozy.HomeSeerDimmers.Apps.Dimmers.Config:
  DimmerLedColorEntityNamePattern: sensor.dimmer_led_{0}_color
  DimmerLedBlinkEntityNamePattern: binary_sensor.dimmer_led_{0}_blink
  LedSyncInterval: 00:10:00
  ZWavePingInterval: 00:00:00 # disabled
```
For supported configuration, see [Config](HomeSeerDimmers/Apps/Dimmers/Config.cs)

7. Make the following configuration changes in the NetDaemon add-on in HA:
```
    app_assembly: HomeSeerDimmers.dll
```
8. Restart NetDaemon add-on.
9. You are done, enjoy!

## Compiling the code

The project uses [.NET 7](https://dotnet.microsoft.com/en-us/download) and is created in VS 2022 but you can also use VS Code with .NET extensions.

Alternativelty, it is also possible to compile from the command line by first opening a command prompt, go to the the root of the repo and then
```
dotnet build --configuration Release
```
