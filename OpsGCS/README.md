# OpsGCS — operational GCS layer on Mission Planner

A purpose-built operational Ground Control Station (GCS) for ArduPlane/QuadPlane, ArduCopter and Rover/Boat. It is a fork of
ArduPilot Mission Planner that keeps full MAVLink and ArduPilot compatibility.
It adds a separate interface layer and changes upstream code as little as possible.

## Design

| Layer | Where | Upstream diff |
|---|---|---|
| Role model, menus, shortcuts, map menu | `OpsGCS/OpsGcsPlugin.cs` | none (compiled into the exe, loaded by `PluginLoader` "self" scan) |
| Health/mission logic (pure, unit-tested) | `OpsGCS/Core/*` | none |
| Dashboard panels | `OpsGCS/UI/*` | none |
| Shortcut hook | `MainV2.ShortcutFilter` | +13 lines in `MainV2.cs` |
| Dark theme | `custom.mpsystheme` (picked up by upstream on first run) | new file + csproj copy rule |
| Deployment config | `opsgcs.json` | new file + csproj copy rule |
| Auto-update off | `app.config` | 2 values blanked |
| CI | `.github/workflows/opsgcs.yml`; upstream jobs guarded to `ArduPilot/MissionPlanner` | 1 line per upstream job |

Hiding is done through upstream `DisplayView` flags wherever they exist. That way upstream's own code removes tabs and pages,
and the fork does not patch the designer files. Those files are where merge conflicts would otherwise build up.

## Roles

- **Operator** (default at every start): Flight Data and Flight Plan only. The Setup, Config, Simulation and Help menus are hidden.
  Flight Data keeps the Quick, Pre-Flight, Actions (simple), Telemetry logs, Messages and Payload tabs, plus OPS.
  Standard flight modes only. Map right-click is limited to `OperatorMapMenuAllowlist`.
  Developer shortcuts are blocked (F4 and Ctrl+F/P/G/X/L/W/Z/T/Y/J).
- **Engineer**: the stock Advanced view. To switch, press Ctrl+Shift+E or the button on the OPS tab, then enter the PIN.
  Engineer mode is disabled until a PIN hash is set.

Set the PIN hash (PowerShell):

```powershell
$pin = Read-Host -AsSecureString "PIN"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pin))
-join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($plain)) | % { $_.ToString("x2") })
```

Paste the output into `EngineerPinSha256` in `opsgcs.json`, which sits next to `MissionPlanner.exe`.

> The role lock is a UI restriction, not a security boundary. Anyone with write access to the install folder or to
> `%USERPROFILE%\Documents\Mission Planner\config.xml` can get around it. Lock down those folders with NTFS permissions
> on fielded laptops.

## Dashboard

- **Health strip** (top of the map): vehicle, mode, arm/failsafe state, and LINK / BATT / GPS / EKF / VIBE tiles.
  Each tile is green, amber or red against the `Thresholds` set in `opsgcs.json`. Hover a tile for detail.
- **Mission strip** (bottom of the map): WP n/N, distance and ETA to the active waypoint, ground speed, altitude,
  distance to home, time in air, and a progress bar with one tick per mission item.
- **OPS tab** (Flight Data left pane): a fleet table showing every vehicle on every open link, plus a health breakdown of
  the active vehicle. Click a row to make that vehicle the active one.

The overlay always shows SI units (m, m/s), whatever display units are set in the Planner settings.

## Build

Building needs Windows. Push to any `opsgcs/**` branch: the **OpsGCS Windows Build** workflow builds `MissionPlanner.sln`,
runs the OpsGCS tests and uploads `OpsGCS-<sha>.zip`. Push a tag `ops-vX.Y.Z` to publish a pre-release.

To build locally, use VS 2022 with `vs2022.vsconfig`. Run `git submodule update --init` first, then
`msbuild -restore -p:Configuration=Release MissionPlanner.sln`.

## Keeping up with upstream

```bash
git remote add upstream https://github.com/ArduPilot/MissionPlanner.git
git fetch upstream
git merge upstream/master     # conflicts are limited to MainV2.cs, MissionPlanner.csproj, app.config and the workflows
```

## Licence

Mission Planner is GPL-3.0, so this fork is GPL-3.0 too. If binaries go to anyone outside your organisation
(for example an industry partner or another nation), the matching source must go with them or be offered to them.
Internal MoD use creates no distribution obligation.
