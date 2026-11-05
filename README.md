# xPilot Audio Normalizer

## Download

- Latest release MSI: https://github.com/cmachler/xpilot-equalizer/releases/latest
- Documentation site: https://cmachler.github.io/xpilot-equalizer/
- Pages source is deployed from `docs/` on pushes to `main` via GitHub Actions.

Live VATSIM voice normalization. Presets/parameters (checkbox, preset picker,
8 sliders, `.eq` commands) plus live level meters and gain readout. DSP runs
on the live headset/speaker audio path via `AudioPipelineHook`; the three
`System.Reflection.Emit*` facades are copied from the installed .NET 10
runtime by `scripts/install-plugin.ps1`.

## Install

Build the MSI (requires the .NET 10 SDK and the WiX v4 CLI, installed
automatically by the build script if missing):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1
```

Then install (per-user, no elevation; xPilot is closed automatically if running):

```powershell
msiexec /i dist\XpilotAudioEqualizer.msi /qn
```

Files land in:

```
%LOCALAPPDATA%\org.vatsim.xpilot\Plugins\XpilotAudioEqualizer\
  XpilotAudioEqualizer.dll
  System.Reflection.Emit*.dll
  normalizer-config.json   # auto-created on first load
```

Silent install: `msiexec /i dist\XpilotAudioEqualizer.msi /qn`
(no `/qn` for the interactive wizard).

Uninstall via Add/Remove Programs ("xPilot Audio Normalizer"), or:

```powershell
msiexec /x dist\XpilotAudioEqualizer.msi /qn
```

A runtime-created `normalizer-config.json` is left behind after uninstall; delete
it manually if desired.

Developer fallback (no MSI): `scripts/install-plugin.ps1` copies the same files
from `publish/` / `dist/XpilotAudioEqualizer/`.

Restart xPilot; the debug log should show:

```
xPilot Audio Normalizer 1.0.0 loaded (Target: -18.0 dBFS). Type .eq to open settings.
```

## Use

In the xPilot message box:

| Command | Effect |
|---|---|
| `.eq` / `.eq show` / `.eq gui` | Open settings window |
| `.eq hide` / `.eq close` | Hide settings window |
| `.eq on` / `.eq off` / `.eq toggle` | Arm / disarm (stored; takes effect once a pipeline hook exists) |
| `.eq target <-30 .. -6>` | Set target loudness in dBFS (marks preset `Custom`) |
| `.eq preset <balanced\|aggressive\|gentle\|night>` | Apply preset |
| `.eq status` | `ON/OFF preset=… target=…dBFS boost=…dB cut=…dB ratio=…:1` (no live gain — no audio tap) |
| `.eq reset` | Back to Balanced defaults |

The settings window shows the armed/off status, an enable checkbox, a preset
dropdown, 8 parameter sliders, and `Reset to Defaults` / `Close`. `Close` and
the window ✕ only hide; state is destroyed on session end. Editing
`normalizer-config.json` by hand works too — it hot-reloads (debounced save,
atomic tmp+stream-copy writes, self-write echo suppression).

## Presets

| Preset | Target | Boost/Cut | Ratio | Attack/Release | Gate open/close | Ceiling |
|---|---|---|---|---|---|---|
| Balanced (default) | −18 dBFS | 18/18 dB | 4.0:1 | 10/350 ms | −42/−47 | −1.0 |
| Aggressive Leveler | −15 dBFS | 22/22 dB | 8.0:1 | 5/250 ms | −40/−45 | −1.0 |
| Gentle / Natural | −20 dBFS | 12/12 dB | 2.5:1 | 20/500 ms | −45/−50 | −1.0 |
| Night Mode | −22 dBFS | 16/24 dB | 6.0:1 | 4/300 ms | −42/−47 | −3.0 |

Any slider move or `.eq target` switches the preset label to `Custom`.

## Verifying it works

1. `.eq status` → expect `ON preset=Balanced target=-18.0dBFS …`.
2. `.eq off` → `.eq on` flips the stored state (window status line + checkbox follow).
3. Hand-edit `normalizer-config.json` while running — `.eq status` reflects it within ~1 s.
4. No audible effect is expected yet: voice audio is untouched until a pipeline hook exists.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| No `loaded` message | Plugin folder name must match DLL name; close xPilot before copying; check `net10.0` runtime loads |
| `.eq` prints `Unknown command` | Hook didn't install — see `.eq commands unavailable` row |
| `[AudioNormalizer] .eq commands unavailable: MainTabViewModel not found.` | xPilot internal layout changed: `MainTabViewModel` / `ProcessCommandLine` / `MessagesAreaViewModel._mainTabViewModel` moved — see `docs/ARCHITECTURE.md` § Dot-command hook |
| `.eq target` prints usage | Value must parse as invariant float in −30..−6 (clamped on apply anyway) |
| Unknown preset | Use `balanced\|aggressive\|gentle\|night`; partial names don't match |
| No audible effect | Expected in this build — no live audio tap is installed yet |

## Build from source

```powershell
dotnet build XpilotAudioEqualizer.sln
dotnet test XpilotAudioEqualizer.sln
```

net10.0 SDK. Tests: pure-DSP facts + hermetic interop facts run anywhere;
bundle-dependent `SkippableFact`s need the local xPilot install (they extract
`AFV.Sdk.Net.dll` / `xPilot.dll` from `…\org.vatsim.xpilot\Application\xPilot.exe`
into `%TEMP%\xpilot-equalizer-smoke` and skip when absent).

For contributors: `AGENTS.md` (agent rules), `docs/ARCHITECTURE.md` (how the
hooks and DSP actually work).

## CI / Releases

Pushing a tag `vX.Y.Z` (or dispatching the `Build MSI` workflow) builds the MSI on
`windows-latest` and attaches it to a GitHub Release. Pages deploys on changes under `docs/`.

GitHub Pages source must be set to "GitHub Actions" in repository settings.
