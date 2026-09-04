<img src="assets/logo-256.png" alt="Datameter" width="96" align="left" hspace="12" vspace="4">

# Datameter

A Windows app that totals your data usage across every network, over any period you pick.

[![Download](https://img.shields.io/badge/Download-latest%20release-4CC2FF?style=for-the-badge)](https://github.com/CollaborativeSketch/Datameter/releases/latest)
[![License](https://img.shields.io/badge/licence-MIT-5CD6A9?style=for-the-badge)](LICENSE)

<br clear="left">

Windows records per-network usage but does not combine it. The Settings → Data usage page shows
one adapter at a time, so a machine that moves between Wi-Fi, a mobile hotspot and Ethernet has no
single figure for what it actually used. Datameter adds them up.

## Install

| Your PC | Download |
| --- | --- |
| Most Windows PCs (Intel / AMD 64-bit) | [Download for x64](https://github.com/CollaborativeSketch/Datameter/releases/latest/download/DatameterSetup-x64.exe) |
| ARM64 (Surface Pro X, Snapdragon laptops) | [Download for ARM64](https://github.com/CollaborativeSketch/Datameter/releases/latest/download/DatameterSetup-arm64.exe) |
| 32-bit Windows | [Download for x86](https://github.com/CollaborativeSketch/Datameter/releases/latest/download/DatameterSetup-x86.exe) |

If you are unsure, choose x64. It is correct for almost every PC.

There is one installer per architecture rather than a single universal one, which keeps each
download to roughly 40 to 60 MB.

Installation is per-user and does not prompt for administrator rights. Each build is
self-contained, so the target machine does not need .NET or the Windows App Runtime installed. The
installers are unsigned, so Windows SmartScreen will warn on first run; choose **More info** then
**Run anyway**.

### Requirements

Windows 10 version 1809 (build 17763) or later, including Windows 11.

`GetNetworkUsageAsync`, the API Datameter is built on, does not exist in earlier versions, and
neither the Windows App SDK nor .NET 9 supports them. Windows 7 and 8.1 cannot be supported
without both a different UI framework and a different source of usage data.

## Features

- Totals for the last 24 hours, 7 days or 30 days, for today, yesterday, this month or last month,
  for the last 12 months, or for a date range you choose. Calendar periods resolve against local
  midnight rather than UTC.
- A proportional bar showing each network's share, with a tile per network beneath it.
- Selecting a network narrows every figure to it. Selecting several combines them.
- A per-application breakdown, equivalent to the one in Settings but summed across all networks
  instead of a single adapter.
- A live speed readout, and a small floating meter that stays above other windows. Drag it
  anywhere on screen; right-click it to hide it, or turn it off in settings.
- A usage chart with a labelled scale down its left edge, so a bar can be read as a figure rather
  than only compared with its neighbours.
- Opens on today the first time it is run, and afterwards on whichever period you last looked at.
- Light, dark, or follow-system appearance, remembered between runs.

## Local database

Windows retains roughly 30 days of usage history. Datameter caches what it reads into a local
SQLite database that only appends, so its history extends past what Windows can still report, and
a network's figures remain after its Wi-Fi profile is deleted.

It can also import history from the "Data usage" Store app by 31229smartApps if that is installed.
That app stores about a year of hourly records, including networks the Windows API can no longer
report. Imported hours never overwrite hours read from Windows.

## How usage is read

`Windows.Networking.Connectivity.ConnectionProfile.GetNetworkUsageAsync`, the same source the
Settings page uses. It requires no elevation and no special capability.

Three measured properties of that API shape the design:

| Property | Measured | Consequence |
| --- | --- | --- |
| Query latency | About 3.1 s per network for a 30-day span | Results are cached; only the delta is re-read |
| Cost against granularity | Hourly costs the same as a bare total | Always fetch hourly and roll up locally |
| Maximum span | About 58 days | Longer requests throw or return zero, so they are clamped |

Per-application figures cannot be cached the same way. That API returns totals for a range with no
per-hour attribution, so each period is a live query and nothing accumulates beyond the retention
Windows itself provides.

Networks are identified by profile name. `ConnectionProfile.NetworkAdapter` throws for a profile
that is not currently available, so an adapter identifier is only known while that network is in
range and cannot be part of a stable identity.

### Live speed

The speed readout comes from somewhere else entirely. `GetNetworkUsageAsync` reports history in
whole hours and cannot answer how fast a connection is running right now, so the meter reads the
byte counters on `NetworkInterface` once a second and differentiates them.

It reads the adapter carrying the internet connection rather than summing every adapter that
happens to be up, because a VPN or a virtual switch would otherwise be counted twice: the same
bytes cross the tunnel and the physical card beneath it.

Nothing measured this way is recorded. The counters reset when the adapter does, which makes them
worth a reading rather than a row in the database.

## Building

Requires the .NET 9 SDK. Visual Studio is not needed; the XAML compiler ships in the Windows App
SDK NuGet package.

```
dotnet build src/Datameter.App/Datameter.App.csproj
```

### Installers

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php), available via
`winget install JRSoftware.InnoSetup`.

```
powershell -File installer\build.ps1
```

This publishes for x64, ARM64 and x86 and builds one installer each into `dist\`. It fails if a
publish is missing its compiled XAML, because `dotnet publish` omits those files for unpackaged
WinUI 3 applications and the resulting installer produces an app that cannot start.

### Diagnostics

`Datameter.Cli` runs the data layer without the UI and prints the same totals.

```
dotnet run --project src/Datameter.Cli            # sync and report
dotnet run --project src/Datameter.Cli -- --networks   # list stored networks
```

## Layout

| Path | Contents |
| --- | --- |
| `src/Datameter.Core` | Usage provider, SQLite cache, sync, archive importer. No UI. |
| `src/Datameter.App` | WinUI 3 application |
| `src/Datameter.Cli` | Headless harness for verifying the data layer |
| `installer/` | Inno Setup script and the multi-architecture build script |
| `assets/` | Logo generator; the icon and PNG are produced by `generate-icon.ps1` |

## Disclaimer

Datameter is free software. It costs nothing and comes with no warranty of any kind.

You install and use it at your own risk. Alexander Akinbiyi accepts no liability for any loss or
damage of any kind arising from installing or using this software, including damage to your
device, your data or your network, or any consequence of relying on the figures it reports.

Datameter reads the usage statistics Windows already records for your account. It sends nothing
anywhere. Everything it reads is stored in a database on your own machine.

The installer presents this disclaimer and requires you to accept it before anything is written to
disk.

## Licence

MIT. See [LICENSE](LICENSE).

Built by Alexander Akinbiyi ([@CollaborativeSketch](https://github.com/CollaborativeSketch)).
