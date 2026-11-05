# xPilot Audio Normalizer

Live VATSIM voice normalization for xPilot 4. Presets, parameters, `.eq` commands,
settings UI, and level meters. Install the MSI; no build tooling required.

- Latest release MSI: https://github.com/cmachler/xpilot-equalizer/releases/latest
- Documentation site: https://cmachler.github.io/xpilot-equalizer/

---

## Install (users)

1. Download `XpilotAudioEqualizer.msi` from the [latest release](https://github.com/cmachler/xpilot-equalizer/releases/latest).
2. Install (per-user, no elevation; xPilot closes automatically if running):
   ```powershell
   msiexec /i XpilotAudioEqualizer.msi /qn
   ```
   Or run the MSI for the interactive wizard.
3. Restart xPilot. The debug log should show:
   ```
   xPilot Audio Normalizer 1.0.0 loaded (Target: -18.0 dBFS). Type .eq to open settings.
   ```

Files land in `%LOCALAPPDATA%\org.vatsim.xpilot\Plugins\XpilotAudioEqualizer\`.

### Uninstall

Use Add/Remove Programs ("xPilot Audio Normalizer"), or:
```powershell
msiexec /x XpilotAudioEqualizer.msi /qn
```
A runtime-created `normalizer-config.json` may remain; delete it manually if desired.

## Use

In the xPilot message box:

| Command | Effect |
|---|---|
| `.eq` / `.eq show` / `.eq gui` | Open settings window |
| `.eq hide` / `.eq close` | Hide settings window |
| `.eq on` / `.eq off` / `.eq toggle` | Arm / disarm normalization |
| `.eq target <-30 .. -6>` | Set target loudness in dBFS (marks preset `Custom`) |
| `.eq preset <balanced\|aggressive\|gentle\|night>` | Apply preset |
| `.eq status` | Show armed state, preset, target, boost/cut, ratio |
| `.eq reset` | Reset to Balanced defaults |

Settings window: armed status, enable checkbox, preset dropdown, 8 parameter
sliders, Reset to Defaults / Close. Close and window ✕ only hide; state is
destroyed on session end. `normalizer-config.json` can be hand-edited and
hot-reloads (~1 s).

## Presets

| Preset | Target | Boost/Cut | Ratio | Attack/Release | Gate open/close | Ceiling |
|---|---|---|---|---|---|---|
| Balanced (default) | −18 dBFS | 18/18 dB | 4.0:1 | 10/350 ms | −42/−47 | −1.0 |
| Aggressive Leveler | −15 dBFS | 22/22 dB | 8.0:1 | 5/250 ms | −40/−45 | −1.0 |
| Gentle / Natural | −20 dBFS | 12/12 dB | 2.5:1 | 20/500 ms | −45/−50 | −1.0 |
| Night Mode | −22 dBFS | 16/24 dB | 6.0:1 | 4/300 ms | −42/−47 | −3.0 |

Any slider move or `.eq target` switches the preset label to `Custom`.

## Verify it works

1. `.eq status` → `ON preset=Balanced target=-18.0dBFS …`.
2. `.eq off` then `.eq on` flips stored state; window checkbox follows.
3. Hand-edit `normalizer-config.json` while running; `.eq status` reflects within ~1 s.
4. No audible effect yet: DSP exists but no live audio tap is installed.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| No `loaded` message | Folder name must match DLL name; close xPilot before copying; check .NET 10 runtime |
| `.eq` prints `Unknown command` | Hook did not install; see architecture docs |
| `.eq commands unavailable: MainTabViewModel not found.` | xPilot internals changed; see `docs/ARCHITECTURE.md` |
| `.eq target` prints usage | Value must be invariant float in −30..−6 |
| Unknown preset | Use `balanced\|aggressive\|gentle\|night` |
| No audible effect | Expected in this build |

---

## Developers

Build from source:
```powershell
dotnet build XpilotAudioEqualizer.sln
dotnet test XpilotAudioEqualizer.sln
```

net10.0 SDK. Tests: pure-DSP facts + hermetic interop facts run anywhere;
bundle-dependent `SkippableFact`s need local xPilot install (extract
`AFV.Sdk.Net.dll`/`xPilot.dll` from `…\org.vatsim.xpilot\Application\xPilot.exe`
into `%TEMP%\xpilot-equalizer-smoke`; skip when absent).

Developer install (no MSI):
```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-plugin.ps1
```
copies `publish/` + vendored Emit facades.

Build MSI locally:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1 -Version 1.0.1
msiexec /i dist\XpilotAudioEqualizer.msi /qn
```

### CI / Releases

- `ci.yml`: build + test on PRs and `main`.
- `build-msi.yml`: on tag `v*` or dispatch, build MSI on `windows-latest`, upload artifact; release only on tag push. Runs on PRs touching installer/src to validate build.
- `pages.yml`: deploys `docs/` to GitHub Pages on `main`; source must be "GitHub Actions" in repo settings.

WiX extensions are installed by the workflow; local builds reuse your cached extensions.

### Notes for contributors

- AGENTS.md: agent rules and trimmed-host constraints.
- docs/ARCHITECTURE.md: reflection hooks, DSP chain, config details.
- `Dsp/` is tested but DSP is not wired to any audio path yet.
