# HomeSeer Dimmers

[![.NET](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/dotnet.yml/badge.svg)](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/github-code-scanning/codeql/badge.svg)](https://github.com/yavuzozge/HomeSeerDimmers/actions/workflows/github-code-scanning/codeql)

The aim of this project is to manage HomeSeer dimmer LEDs from Home Assistant easily.

The basic premise is to define some entities in Home Assistant (I typically use template entities) that represents what the color and blink state of the LEDs should be, 
have this app monitor those entities for state changes and sync those changes to the HomeSeer dimmer LEDs.

Couple of highlights:
1. Currently HS-WD200+ and HS-WX300 are supported, though HS-WX300 is not tested.
2. It supports periodic syncs for dimmers. I recommend this to be enabled since sometimes Z-Wave network may drop messages.
3. It supports periodic pings for Z-Wave devices. It becomes useful if the device sends state updates and your Z-Wave network may drop those messages. One example is that you may have a Z-Wave door lock that sends status updates when it is unlocked/locked and you may want to set the dimmer LEDs to change color accordingly. In such cases, pinging those devices would refresh their states in HA even if any previously sent message was lost, and hopefully keep your LEDs consistent with the device state  

This project is created in C# using [NetDaemon](https://netdaemon.xyz/) and .NET 7.

## Getting started

Here is the basic configuration steps:
1. Make sure that the HomeSeer dimmers are set to "status" mode. Pls refer to the dimmer docs  I recommend setting blink frequency to 500ms
2. Configure [NetDaemon](https://netdaemon.xyz/). I use it as an HA add-on
3. Define your entities to HA. Here are examples using templated enitites (in `template.yaml`):
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
To see supported colors, pls refer to [LedStatusColor](HomeSeerDimmers/Apps/Dimmers/LedStatusColor.cs) enum.

4. Create 'settings.yaml' in the Apps/Dimmers directory, and set the configuration of the app to something like:
```YAML
Ozy.HomeSeerDimmers.Apps.Dimmers.Config:
  DimmerLedColorEntityNamePattern: sensor.dimmer_led_{0}_color
  DimmerLedBlinkEntityNamePattern: binary_sensor.dimmer_led_{0}_blink
  LedSyncInterval: 00:10:00
  ZWavePingInterval: 00:00:00 # disabled
```
To see supported configuration items, see [Config](HomeSeerDimmers/Apps/Dimmers/Config.cs)

5. Deploy the app to NetDaemon add-on. Don't forget to make the following configuration changes to the add-on:
```
    app_assembly: HomeSeerDimmers.dll
```
