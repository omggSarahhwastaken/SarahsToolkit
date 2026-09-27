# Sarah's Toolkit

A tabbed Windows 11 cleanup, debloat, and optimization app. The GUI companion to
Sarah's Optimizer — same aggressive-cleanup / conservative-debloat philosophy, in a
point-and-click package.

All code here is original. (Winhance was used only as behavioral/design inspiration;
its PolyForm Shield license forbids forking, so nothing of its source is in this repo.)

## Tabs

- **Cleanup** — Quick Clean (fast: temps, caches, shaders, browsers) and Full Clean
  (everything + update caches + log bloat), with live progress and a freed-space readout.
- **Debloat** — the 41-app conservative list from Sarah's Optimizer. Only checked apps
  are removed; Edge, Copilot, Xbox / Game Bar, OneDrive, Solitaire, Clipchamp,
  Media Player, Films & TV, and Office Hub are never listed.
- **Optimize** — data-driven registry tweaks (privacy, gaming, performance) with
  one-click apply/revert and live state detection.
- **Customize** — theme, taskbar, Explorer, and Start menu tweaks, same engine.
- **Tools** — DNS selector (Cloudflare/Google/Quad9/OpenDNS), service optimizer,
  SSD ReTrim, network rescue, and a GitHub-releases update check.

## Data-driven

All behavior lives in JSON under `src/SarahsToolkit/Data/` and is copied next to the
exe at build time, so lists can evolve without recompiling:

- `tweaks.json` — 13 tweaks: apply/revert registry ops + a state check each
- `debloat.json` — 41 Appx package patterns with friendly names
- `cleanup.json` — 8 cleanup categories with paths (`%ENV%` expansion, `*` wildcards)

## Build

Requires **Windows 10/11** and the **.NET 8 SDK**:

```powershell
dotnet build -c Release
```

The exe lands in `src/SarahsToolkit/bin/Release/net8.0-windows/`. It carries an
administrator manifest, so Windows will prompt for elevation on launch (expected —
SmartScreen may also flag it since it's unsigned).

## Principles

- No local artifacts: helper PowerShell is written to a temp file, run once, deleted.
- Quick Clean stays the fast path on purpose.
- Hardware-universal: no vendor-specific assumptions beyond existence checks.
