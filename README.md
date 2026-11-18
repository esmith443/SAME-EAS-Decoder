<p align="center">
  <img src="docs/banner.png" alt="SAME / EAS Decoder" width="100%">
</p>

<p align="center">
  <a href="https://github.com/esmith443/SAME-EAS-Decoder/releases/latest"><img src="https://img.shields.io/github/v/release/esmith443/SAME-EAS-Decoder?style=flat-square&color=4DA3FF&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0B2A55?style=flat-square" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/.NET-10-1565C0?style=flat-square" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-WPF-2E9BEA?style=flat-square" alt="WPF">
  <img src="https://img.shields.io/badge/mode-receive%20only-3DD68C?style=flat-square" alt="Receive only">
</p>

A Windows app that listens to any audio source and decodes Emergency Alert System messages in real time. It decodes SAME headers, EAS and NOAA Weather Radio attention tones, and end-of-message bursts, then maps the affected area.

![Receiving an alert](docs/receiving-alert.gif)

## Download

Get **[SameEasDecoder.exe](https://github.com/esmith443/SAME-EAS-Decoder/releases/latest/download/SameEasDecoder.exe)** from the [latest release](https://github.com/esmith443/SAME-EAS-Decoder/releases/latest). It is a single file for 64-bit Windows 10 and 11 with nothing to install, not even .NET.

The exe is not code-signed, so Windows SmartScreen may warn the first time you run it. Choose **More info**, then **Run anyway**.

## Features

- **Any audio source**: line-in, microphone, or system audio (web streams, SDR software)
- **Instant alerts**: an alert appears from the first SAME burst and is confirmed as the repeats arrive
- **Error tolerant**: damaged headers are recovered by combining the three repeats bit by bit and repairing characters that break the SAME format
- **Maps**: affected counties, county sub-areas, whole states and nationwide alerts are drawn on an offline map
- **Live meters and spectrum**: indicators for SAME data, the EAS tone (853 + 960 Hz) and the NWR tone (1050 Hz)
- **Verbose activity log**: raw bytes, damaged bits and a burst-by-burst comparison for every alert
- **History**: every alert is kept with its map, activity log and recording; search, filter and export to CSV
- **Recording**: each alert is saved as a WAV file and plays back through the meters and spectrum, with live listening paused so playback is never decoded as a new alert

## How it works

![Listen, detect, decode, verify, map](docs/how-it-works.png)

![Inside a SAME header](docs/same-header.png)

## Screenshots

| History | Map |
|---|---|
| ![History](docs/history.png) | ![Map](docs/map.png) |

![Live view](docs/live.png)

## Building from source

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet run --project src/SameEasDecoder
```

To build the same single-file exe as the release:

```
dotnet publish src/SameEasDecoder -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## Notes

This is a receive-only decoder. It does not generate or transmit SAME or EAS signals.

Map data comes from [us-atlas](https://github.com/topojson/us-atlas) (ISC licence), built from US Census Bureau boundaries.
