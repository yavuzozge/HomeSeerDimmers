# HomeSeer Dimmers
The aim of this project is to support setting HomeSeer dimmer LEDs/blink states in Home Assistant easily.

The basic premise of the project is to define some entities in Home Assistant (I typically use templated entities) that represents the LED colors and LED blink states 
and have this app monitor for state changes and then "sync" those changes to the LEDs

This project is created using [NetDaemon](https://netdaemon.xyz/)  and .NET 7.

## Getting started

Here is the basic configuration steps:
1. Configure [NetDaemon](https://netdaemon.xyz/). I use it as an HA add-on
2. Add your templated entities to HA. Here is an example:
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
3. Create 'settings.yaml' for the app and set the configuration of the app as follows:
```YAML
Ozy.HomeSeerDimmers.Apps.Dimmers.Config:
  DimmerLedColorEntityNamePattern: sensor.dimmer_led_{0}_color
  DimmerLedBlinkEntityNamePattern: binary_sensor.dimmer_led_{0}_blink
  LedSyncInterval: 00:10:00
  ZWavePingInterval: 00:00:00 # disabled
```
4. Deploy the app to NetDaemon add-on. I made the following config changes:
```
    app_assembly: HomeSeerDimmers.dll
```
