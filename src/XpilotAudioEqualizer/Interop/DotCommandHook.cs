using System.Reflection;
using Vatsim.Xpilot.PluginSdk;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;

namespace XpilotAudioEqualizer.Interop;

internal sealed class DotCommandHook : IDisposable
{
    private readonly IBroker _broker;
    private readonly ConfigStore _config;
    private readonly MasterAudioProcessor _processor;
    private readonly Func<Action> _showWindow;
    private readonly Func<Action> _hideWindow;
    private bool _disposed;

    private object? _originalDotCommandProcessor;
    private object? _proxy;
    private object? _xplaneSubscription;
    private Delegate? _xplaneHandler;
    private IDisposable? _eventBusXplaneSub;

    public DotCommandHook(IBroker broker, ConfigStore config, MasterAudioProcessor processor, Func<Action> showWindow, Func<Action> hideWindow)
    {
        _broker = broker;
        _config = config;
        _processor = processor;
        _showWindow = showWindow;
        _hideWindow = hideWindow;
    }

    public bool Install()
    {
        try
        {
            if (!InstallUiDotCommandProcessor())
            {
                _broker.PostDebugMessage("[AudioNormalizer] .eq commands unavailable: dot command processor not found.");
                return false;
            }
            InstallXplaneSubscription();
            return true;
        }
        catch (Exception ex)
        {
            _broker.PostDebugMessage($"[AudioNormalizer] .eq hook failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public Task HandleCommand(string command, Func<string, Task>? resultHandler, object? state)
    {
        var cmd = (command ?? string.Empty).Trim();
        if (!cmd.Equals(".eq", StringComparison.OrdinalIgnoreCase) &&
            !cmd.StartsWith(".eq ", StringComparison.OrdinalIgnoreCase))
        {
            return ForwardToOriginal(cmd, resultHandler);
        }

        var rest = cmd.Length <= 3 ? string.Empty : cmd[3..].Trim();
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "show";

        switch (verb)
        {
            case "show":
            case "gui":
            case "":
                _showWindow()( );
                break;
            case "hide":
            case "close":
                _hideWindow()();
                break;
            case "on":
                _config.Update(s => s with { Enabled = true });
                _broker.PostDebugMessage("[AudioNormalizer] Normalization ON.");
                break;
            case "off":
                _config.Update(s => s with { Enabled = false });
                _broker.PostDebugMessage("[AudioNormalizer] Normalization OFF.");
                break;
            case "toggle":
            {
                var next = _config.Update(s => s with { Enabled = !s.Enabled });
                _broker.PostDebugMessage($"[AudioNormalizer] Normalization {(next.Enabled ? "ON" : "OFF")}.");
                break;
            }
            case "target":
            {
                if (parts.Length < 2 || !double.TryParse(parts[1], out var db))
                {
                    _broker.PostDebugMessage("[AudioNormalizer] Usage: .eq target <-30 .. -6> (dBFS)");
                    break;
                }
                var next = _config.Update(s => s with { TargetDbFs = db, PresetName = "Custom" });
                _broker.PostDebugMessage($"[AudioNormalizer] Target loudness {next.TargetDbFs:0.0} dBFS.");
                break;
            }
            case "preset":
            {
                if (parts.Length < 2)
                {
                    _broker.PostDebugMessage("[AudioNormalizer] Usage: .eq preset <balanced|aggressive|gentle|night>");
                    break;
                }
                var name = parts[1].ToLowerInvariant() switch
                {
                    "balanced" => NormalizerPresets.Balanced,
                    "aggressive" => NormalizerPresets.Aggressive,
                    "gentle" => NormalizerPresets.Gentle,
                    "night" => NormalizerPresets.Night,
                    _ => parts[1],
                };
                if (NormalizerPresets.TryGet(name, out var preset) ||
                    NormalizerPresets.TryGet(parts[1], out preset))
                {
                    _config.Update(_ => preset);
                    _broker.PostDebugMessage($"[AudioNormalizer] Preset '{preset.PresetName}' applied.");
                }
                else
                {
                    _broker.PostDebugMessage("[AudioNormalizer] Unknown preset. Use balanced|aggressive|gentle|night.");
                }
                break;
            }
            case "status":
            {
                var s = _config.Current;
                var t = _processor.GetAggregateTelemetry(headset: true);
                _broker.PostDebugMessage(
                    $"[AudioNormalizer] {(s.Enabled ? "ON" : "OFF")} preset={s.PresetName} target={s.TargetDbFs:0.0}dBFS " +
                    $"boost={s.MaxBoostDb:0.0}dB cut={s.MaxCutDb:0.0}dB ratio={s.CompressionRatio:0.0}:1 gain={t.CurrentGainDb:0.0}dB");
                break;
            }
            case "reset":
            {
                var balanced = NormalizerPresets.Get(NormalizerPresets.Balanced);
                _config.Update(_ => balanced);
                _broker.PostDebugMessage("[AudioNormalizer] Reset to Balanced defaults.");
                break;
            }
            default:
                _broker.PostDebugMessage("[AudioNormalizer] Usage: .eq [show|hide|on|off|toggle|target <dB>|preset <name>|status|reset]");
                break;
        }
        return Task.CompletedTask;
    }

    private Task ForwardToOriginal(string command, Func<string, Task>? resultHandler)
    {
        try
        {
            if (_originalDotCommandProcessor is null)
            {
                resultHandler?.Invoke(command);
                return Task.CompletedTask;
            }
            var m = _originalDotCommandProcessor.GetType().GetMethod("Process", BindingFlags.Public | BindingFlags.Instance);
            if (m is null)
            {
                return Task.CompletedTask;
            }
            var t = m.Invoke(_originalDotCommandProcessor, [command, resultHandler]);
            return t as Task ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _broker.PostDebugMessage($"[AudioNormalizer] Command error: {ex.GetBaseException().Message}");
            return Task.CompletedTask;
        }
    }

    private bool InstallUiDotCommandProcessor()
    {
        // Path 1: MainTabViewModel._dotCommandProcessor (owns UI command routing).
        var mainTab = FindMainTabViewModel();
        FieldInfo? vmField = null;
        if (mainTab is not null)
        {
            vmField = mainTab.GetType().GetField("_dotCommandProcessor", BindingFlags.NonPublic | BindingFlags.Instance);
            if (vmField is not null)
            {
                _originalDotCommandProcessor ??= vmField.GetValue(mainTab);
            }
        }

        // Path 2: ServiceProvider._IDotCommandProcessor singleton.
        var serviceProvider = FindServiceProvider();
        FieldInfo? spField = null;
        if (serviceProvider is not null)
        {
            spField = serviceProvider.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType.Name.Contains("IDotCommandProcessor", StringComparison.Ordinal));
            if (spField is not null)
            {
                _originalDotCommandProcessor ??= spField.GetValue(serviceProvider);
            }
        }

        if (_originalDotCommandProcessor is null)
        {
            return false;
        }

        _proxy ??= DynamicProxyFactory.CreateDotCommandProxy(HandleCommand, null);

        try
        {
            vmField?.SetValue(mainTab, _proxy);
        }
        catch { }
        try
        {
            spField?.SetValue(serviceProvider, _proxy);
        }
        catch { }
        return true;
    }

    private object? FindMainTabViewModel()
    {
        try
        {
            var appType = FindType("Vatsim.Xpilot.App");
            var currentProp = appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            var app = currentProp?.GetValue(null);
            var lifetimeProp = app?.GetType().GetProperty("ApplicationLifetime");
            var lifetime = lifetimeProp?.GetValue(app);
            var mainWindowProp = lifetime?.GetType().GetProperty("MainWindow");
            var mainWindow = mainWindowProp?.GetValue(lifetime);
            if (mainWindow is null)
            {
                return null;
            }
            var contentProp = mainWindow.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name is "Messages" or "MessagesAreaControl" or "Content");
            var messagesArea = contentProp?.GetValue(mainWindow);
            var dcProp = messagesArea?.GetType().GetProperty("DataContext");
            var messagesVm = dcProp?.GetValue(messagesArea);
            if (messagesVm is null)
            {
                // Fall back: MainWindow.DataContext -> MainWindowViewModel -> Messages view model.
                var mainDc = mainWindow.GetType().GetProperty("DataContext")?.GetValue(mainWindow);
                messagesVm = mainDc;
            }
            if (messagesVm is null)
            {
                return null;
            }
            // MessagesAreaViewModel._mainTabViewModel
            var mainTabField = messagesVm.GetType().GetField("_mainTabViewModel", BindingFlags.NonPublic | BindingFlags.Instance);
            var mainTab = mainTabField?.GetValue(messagesVm);
            if (mainTab is not null)
            {
                return mainTab;
            }
            // Already the MainTabViewModel itself?
            if (messagesVm.GetType().Name.Contains("MainTabViewModel", StringComparison.Ordinal))
            {
                return messagesVm;
            }
            // Scan Tabs collection for MainTabViewModel.
            var tabsProp = messagesVm.GetType().GetProperty("Tabs");
            if (tabsProp?.GetValue(messagesVm) is System.Collections.IEnumerable tabs)
            {
                foreach (var t in tabs)
                {
                    if (t?.GetType().Name.Contains("MainTabViewModel", StringComparison.Ordinal) == true)
                    {
                        return t;
                    }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static object? FindServiceProvider()
    {
        try
        {
            var appType = FindType("Vatsim.Xpilot.App");
            // (App._serviceProvider lookup handled below via ServiceProvider.Instance.)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? spType = null;
                try { spType = asm.GetType("Vatsim.Xpilot.Container.ServiceProvider", throwOnError: false); } catch { }
                if (spType is null)
                {
                    continue;
                }
                var instProp = spType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(p => p.PropertyType == spType);
                var inst = instProp?.GetValue(null);
                if (inst is not null)
                {
                    return inst;
                }
            }
            // Instance on App object.
            var currentProp = appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            var app = currentProp?.GetValue(null);
            if (app is not null)
            {
                var f = app.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(f => f.FieldType.Name.Contains("ServiceProvider", StringComparison.Ordinal));
                return f?.GetValue(app);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void InstallXplaneSubscription()
    {
        try
        {
            var busType = FindType("Vatsim.Xpilot.Events.EventBus");
            var instanceProp = busType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var bus = instanceProp?.GetValue(null);
            var cmdType = FindType("Vatsim.Xpilot.Events.ProcessXplaneDotCommand");
            if (bus is null || cmdType is null)
            {
                return;
            }

            // Dispose the original DotCommandProcessor subscription so .eq from X-Plane
            // is not consumed as "Unknown command" before our handler runs.
            try
            {
                var subField = _originalDotCommandProcessor?.GetType()
                    .GetField("_processXplaneDotCommandSubscription", BindingFlags.NonPublic | BindingFlags.Instance);
                _xplaneSubscription = subField?.GetValue(_originalDotCommandProcessor);
            }
            catch { }

            Action<object> handler = OnXplaneDotCommand;
            _xplaneHandler = handler;
            var subscribe = busType!.GetMethod("Subscribe", [typeof(Type), typeof(Action<object>)]);
            _eventBusXplaneSub = subscribe?.Invoke(bus, [cmdType, handler]) as IDisposable;
        }
        catch
        {
            // X-Plane path is optional; UI path already covers the desktop client.
        }
    }

    private void OnXplaneDotCommand(object msg)
    {
        try
        {
            var cmdProp = msg.GetType().GetProperty("Command");
            var command = cmdProp?.GetValue(msg) as string ?? string.Empty;
            if (!command.Equals(".eq", StringComparison.OrdinalIgnoreCase) &&
                !command.StartsWith(".eq ", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _ = HandleCommand(command, null, null);
            // Swallow: dispose the original subscription lazily on first .eq so the
            // original "Unknown command" error is never published for .eq.
            try { (_xplaneSubscription as IDisposable)?.Dispose(); _xplaneSubscription = null; } catch { }
        }
        catch { }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = null;
            try { t = asm.GetType(fullName, throwOnError: false); } catch { }
            if (t is not null)
            {
                return t;
            }
        }
        return Type.GetType(fullName, throwOnError: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { (_eventBusXplaneSub as IDisposable)?.Dispose(); } catch { }
        try
        {
            // Best-effort restore of original processor.
            var mainTab = FindMainTabViewModel();
            var vmField = mainTab?.GetType().GetField("_dotCommandProcessor", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mainTab is not null && _originalDotCommandProcessor is not null)
            {
                try { vmField?.SetValue(mainTab, _originalDotCommandProcessor); } catch { }
            }
            var sp = FindServiceProvider();
            var spField = sp?.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType.Name.Contains("IDotCommandProcessor", StringComparison.Ordinal));
            if (sp is not null && _originalDotCommandProcessor is not null)
            {
                try { spField?.SetValue(sp, _originalDotCommandProcessor); } catch { }
            }
        }
        catch { }
    }
}
