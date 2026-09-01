<img src="assets/logo-256.png" alt="Datameter" width="96" align="left" hspace="12" vspace="4">

# Datameter

A Windows app that totals your data usage across **every** network, over any period you pick.

[![Download](https://img.shields.io/badge/Download-latest%20release-4CC2FF?style=for-the-badge)](https://github.com/CollaborativeSketch/Datameter/releases/latest)
[![License](https://img.shields.io/badge/licence-MIT-5CD6A9?style=for-the-badge)](LICENSE)

<br clear="left">


Windows already records this. It just won't add it up: the Settings → Data usage page shows one
adapter at a time and offers no way to combine them. On the machine this was built for, Settings
reported 96.87 GB for the last 30 days while the true figure across all networks was **167.61 GB** —
42% of the traffic never appeared on that screen.

## Install

Grab the installer for your architecture from the
[latest release](https://github.com/CollaborativeSketch/Datameter/releases/latest):

| Your PC | Download |
| --- | --- |
| Most Windows PCs (Intel / AMD 64-bit) | `DatameterSetup-1.0.0-x64.exe` |
| ARM64 (Surface Pro X, Snapdragon laptops) | `DatameterSetup-1.0.0-arm64.exe` |
| 32-bit Windows | `DatameterSetup-1.0.0-x86.exe` |

Separate builds rather than one universal installer, so a download is around 60 MB instead of
160 MB — which seemed like the right call for an app about not wasting data.

It installs per-user, so there is **no admin prompt**. Each build is self-contained: the target
machine needs neither .NET nor the Windows App Runtime. The installer is unsigned, so Windows
SmartScreen will show "Windows protected your PC" the first time — choose **More info → Run
anyway**.

### Requirements

**Windows 10 version 1809 (build 17763) or later, and Windows 11.** That floor is not a choice:
`GetNetworkUsageAsync`, the API this app is built on, does not exist before it, and neither the
Windows App SDK nor .NET 9 will run there. **Windows 7 and 8.1 are not supported and cannot be** —
it would need both a different UI framework and an entirely different source for the usage data.

## What it does

- **One honest total** for 24 hours, 7 days, 30 days, this month, last month, 12 months, or a
  custom date range — with today and yesterday resolved against your local midnight, not UTC's.
- **A contribution bar** showing what each network is responsible for, with tiles below it.
- **Click any network** to narrow to it; select several to combine them.
- **Per-app breakdown**, the same view Settings gives, but summed across all networks rather than
  one adapter.
- **Light / dark / system** theme, remembered between runs.

## Why it keeps its own database

Windows retains roughly 30 days of usage history and discards the rest. Datameter caches what it
reads into a local SQLite database that only ever appends, so it accumulates history well past what
Windows itself can answer for — and a network's figures survive you forgetting that Wi-Fi.

It can also **import history from the "Data usage" Store app** by 31229smartApps, if you have it.
That app keeps about a year of hourly records, including networks whose Wi-Fi profiles have since
been deleted and which the Windows API can no longer report at all. Imported hours never overwrite
hours read from Windows.

## How it reads usage

`Windows.Networking.Connectivity.ConnectionProfile.GetNetworkUsageAsync` — the same source the
Settings page uses. No elevation, no special capability, no driver.

Three measured constraints shape the design:

| Constraint | Measured | Consequence |
| --- | --- | --- |
| Query latency | ~3.1 s per network for a 30-day span | Everything is cached; only the delta is re-read |
| Cost vs. granularity | Hourly costs the same as a bare total | Always fetch hourly, roll up locally |
| Maximum span | ~58 days | Longer requests throw, or silently return zero — so they are clamped |

Per-app figures can't be cached this way: that API returns totals for a range with no per-hour
attribution, so each period is a live query and nothing accumulates beyond Windows' own retention.

## Building

Requires the **.NET 9 SDK**. No Visual Studio needed — the XAML compiler ships in the Windows App
SDK NuGet package.

```
dotnet build src/Datameter.App/Datameter.App.csproj
```

### Installers

Needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) — `winget install JRSoftware.InnoSetup`.

```
powershell -File installer\build.ps1
```

Publishes for x64, ARM64 and x86 and builds one installer each into `dist\`. The script fails
loudly if the compiled XAML is missing from a publish, because `dotnet publish` drops it for
unpackaged WinUI 3 apps and the result installs happily and then dies on launch.

## Layout

| Path | What it holds |
| --- | --- |
| `src/Datameter.Core` | Usage provider, SQLite cache, sync, archive importer — no UI |
| `src/Datameter.App` | WinUI 3 app |
| `src/Datameter.Cli` | Headless harness that prints the same totals, for verifying the data layer |
| `installer/` | Inno Setup script and the multi-architecture build script |
| `assets/` | Logo generator — the `.ico` and PNG are produced from `generate-icon.ps1` |

## Licence

MIT — see [LICENSE](LICENSE).

Built by **Alexander Akinbiyi** ([@CollaborativeSketch](https://github.com/CollaborativeSketch)).
