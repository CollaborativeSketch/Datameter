<img src="assets/logo-256.png" alt="Datameter" width="96" align="left" hspace="12" vspace="4">

# Datameter

A Windows 11 app that totals your data usage across **every** network, over any period you pick.

<br clear="left">


Windows already records this. It just won't add it up: the Settings → Data usage page shows one
adapter at a time and offers no way to combine them. On the machine this was built for, Settings
reported 96.87 GB for the last 30 days while the true figure across all networks was **167.61 GB** —
42% of the traffic never appeared on that screen.

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

### Installer

```
dotnet publish src/Datameter.App/Datameter.App.csproj -c Release -r win-x64
iscc /DPublishDir="<full path to the publish folder>" installer\Datameter.iss
```

Produces a per-user installer that needs no admin rights. The app is published self-contained, so
the target machine needs neither .NET nor the Windows App Runtime installed.

## Layout

| Path | What it holds |
| --- | --- |
| `src/Datameter.Core` | Usage provider, SQLite cache, sync, archive importer — no UI |
| `src/Datameter.App` | WinUI 3 app |
| `src/Datameter.Cli` | Headless harness that prints the same totals, for verifying the data layer |
| `installer/` | Inno Setup script |

## Licence

MIT — see [LICENSE](LICENSE).

Built by **Alexander Akinbiyi** ([@CollaborativeSketch](https://github.com/CollaborativeSketch)).
