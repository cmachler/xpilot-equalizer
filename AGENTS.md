# AGENTS.md — x-pilot-equalizer

## What this is

xPilot plugin (net10.0) that normalizes VATSIM voice audio. No public xPilot
plugin API for audio/dot-commands exists, so `Interop/` hooks the live runtime
via reflection + a DynamicMethod-based `MethodInfo.Invoke` replacement. DSP
itself is pure and fully testable; interop is best-effort and must never crash
the host.

> Trimmed-host constraint: xPilot ships as a trimmed single-file bundle. Probing
> its manifest shows no FileSystem.Watcher, no mtime APIs, no
> `MethodBase.Invoke`, no `Volatile` float/bool/long overloads, and a hollowed
> System.Text.Json (serializer core gutted). So: config uses hand-rolled JSON
> (`NormalizerJson`), hot-reload polls by content compare, `ReflectionInvoke`
> replaces `MethodInfo.Invoke` (BCL/host-domain signatures only), and
> `SpanAction` is locally defined (no System.Buffers facade). Never reintroduce
> these dependencies. Three Emit facades are copied from the installed .NET 10 runtime by scripts/install-plugin.ps1 (not vendored in the repo). The audio hook is live again.

## Layout

- `src/xPilot.PluginSdk/` — hand-written `IBroker`/`IPlugin` contracts matching
  xPilot's real plugin surface. `AssemblyVersion 0.1.0.0`, `DebugType embedded`.
  Do not rename namespace `Vatsim.Xpilot.PluginSdk`.
- `src/XpilotAudioEqualizer/` — the plugin. `AssemblyName XpilotAudioEqualizer`,
  `AllowUnsafeBlocks`, Avalonia `PrivateAssets=all` + `ExcludeAssets=runtime`
  (UI resolved late from host), SDK `Private=false` + `ExcludeAssets=runtime`.
  - `AudioEqualizerPlugin.cs` — `IPlugin` entry. `Initialize` returns silently on
    `ApiVersion < 0.1` or any throw; posts one `PostDebugMessage` on success.
    Window creation/show/hide go through `*Late` NoInlining methods so Avalonia
    types never load at JIT time on the plugin class.
  - `Dsp/` — pure audio. `ChannelNormalizerDsp` (48 kHz, 960-sample nominal
    frame, frame-duration-scaled smoothing), `MasterAudioProcessor` (4 channels:
    headset L/R + speaker L/R; static `SpanAction<float,object>` entry points,
    locally defined — no System.Buffers facade), `AudioDb`,
    `NormalizerTelemetry` (plain-field snapshot; no `Volatile` — the
    float/bool/long overloads are trimmed from the host runtime).
  - `Config/` — `NormalizerSettings` immutable record with `Clamp()`;
    `ConfigStore` (file = `<dll-dir>/normalizer-config.json`, 1 s
    content-compare poll hot-reload — mtime APIs are trimmed — debounced save
    timer, `FileStream`-only I/O); `NormalizerPresets`
    (Balanced/Aggressive/Gentle/Night); `NormalizerJson` hand parser/serializer
    (System.Text.Json serializer core is trimmed from the host bundle).
  - `Interop/` — `DotCommandHook` (wraps `MainTabViewModel`'s inner `execute`
    delegate + captures `ProcessCommandLine` for the forward path and
    `_dotCommandProcessor` for the X-Plane bus path; forwards non-`.eq` to
    original), `ReflectionInvoke` (DynamicMethod-based `MethodInfo.Invoke`
    replacement — host-framework signature types don't resolve from our load
    context, so `Dispatcher.Post` falls back to a direct call).
  - `Ui/NormalizerWindow.cs` — code-built Avalonia window (no XAML): status
    line, enable checkbox, preset dropdown, 8 parameter sliders, telemetry
    meters/timer, `Reset to Defaults` / `Close`. Close cancels into Hide unless
    `_allowDestroy` (set on session end).
  tests + `RuntimeInteropSmokeTests` (extract `AFV.Sdk.Net.dll`/`xPilot.dll`
  from local xPilot single-file bundle into `%TEMP%\\xpilot-equalizer-smoke`;
- `scripts/install-plugin.ps1` — copies `publish/` (plugin DLL plus three
  vendored facades — three Emit facades copied from the installed .NET 10
  runtime) to `%LOCALAPPDATA%\org.vatsim.xpilot\Plugins\XpilotAudioEqualizer\`.

## Build / test / install

```powershell
dotnet build XpilotAudioEqualizer.sln
dotnet test XpilotAudioEqualizer.sln
dotnet publish src/XpilotAudioEqualizer/XpilotAudioEqualizer.csproj -c Release -o publish
powershell -ExecutionPolicy Bypass -File scripts/install-plugin.ps1
```

Close xPilot before install (DLL lock). Restart xPilot, expect
`xPilot Audio Normalizer 1.0.0 loaded ... Type .eq to open settings.`
Then: `.eq status` → `.eq off` → `.eq on` → `.eq show` → `.eq hide`.
Config-only build: no meters, no live gain in status, no audible effect.
`Dsp/` is tested but not wired to any audio path yet.

## Rules for agents

- **Never crash the host.** Every interop resolve/install/post is try/catch with
  a `PostDebugMessage("[AudioNormalizer] ...")` fallback. Keep that pattern for
  any new reflection/proxy code.
- **DSP: no allocation on audio path.** `Span<float>` in place, single-pass
  loops; buffer-size-invariant timing via `1000.0 * n / SampleRate` scaling.
  `ProcessChannel` clamps settings first.
- **Settings record discipline:** new knobs go in `NormalizerSettings` with
  `Clamp()` bounds + `SettingsEqual` update + preset defaults + `.eq status`
  line (`DotCommandHook.HandleCommand` status case).
- **UI types stay late-bound.** Never reference Avalonia types from
  `AudioEqualizerPlugin` fields/method bodies that JIT at load; use the
  existing `CreateWindowLate/ShowWindowLate/HideWindowLate/CloseWindowLate`
  pattern and `FindDispatcherType`/`FindPostMethod` for thread hops.
- **Dot commands:** verbs `show|hide|on|off|toggle|target|preset|status|reset`;
  unknown input posts usage; non-`.eq` forwards via the captured
  `ProcessCommandLine` delegate (UI path) or `_originalDotCommandProcessor`
  (X-Plane bus path). Install wraps the relay's inner `execute` field —
  dispose restores that same field.
- **Trimmed-host discipline:** System.Text.Json serializer core, `Volatile`
  float/bool/long overloads, tuple deconstruction in hot paths, mtime APIs,
  and `MethodBase.Invoke` are unavailable in-host. Prefer hand-rolled parsing,
  plain field access, direct member access, content-compare polling, and
  `ReflectionInvoke` (BCL/host-domain signatures only).
- **Tests:** DSP tests synthesize sine frames at calibrated dBFS and loop
  ~25 frames to settle envelopes before asserting. Bundle-dependent interop
  facts skip (not fail) without the local xPilot bundle; the rest run
  hermetically with fakes — keep it that way.
- **No second conventions:** code-built Avalonia (no XAML), hand-rolled config
  JSON (`NormalizerJson`), file config next to DLL.
