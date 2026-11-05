using System.Collections;
using System.Globalization;
using System.Reflection;
using Vatsim.Xpilot.PluginSdk;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;

namespace XpilotAudioEqualizer.Interop;

internal sealed class DotCommandHook : IDisposable
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    private readonly IBroker _broker;
    private readonly ConfigStore _config;
    private readonly MasterAudioProcessor _processor;
    private readonly Func<Action> _showWindow;
    private readonly Func<Action> _hideWindow;
    private bool _disposed;
    // The UI path routes through MainTabViewModel.ProcessCommandLine(string).
    // That method is async void-ish (Task-returning) and invoked from the UI
    // command pipeline; the clean seam is to wrap the MainTabViewModel's own
    // command delegate instead of implementing IDotCommandProcessor (whose
    // 3-arg Process overload shape the trimmed host may not honor for proxies).
    private object? _mainTabViewModel;
    private Delegate? _originalCommandDelegate;
    private MethodInfo? _originalProcessCommandLine;
    private Func<string, Task>? _forwardToViewModel;
    private MethodInfo? _originalOnXplaneDotCommand;
    private object? _originalDotCommandProcessor;
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
            if (!InstallMainTabWrapper())
            {
                _broker.PostDebugMessage("[AudioNormalizer] .eq commands unavailable: MainTabViewModel not found.");
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


    /// <summary>
    /// Entry point for .eq commands from any path (UI wrapper, X-Plane bus).
    /// Non-.eq commands forward to the original handler.
    /// </summary>
    public Task HandleCommand(string command, Func<string, Task>? resultHandler)
    {
        try
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
                if (parts.Length < 2 || !double.TryParse(parts[1],
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out var db))
                {
                    _broker.PostDebugMessage("[AudioNormalizer] Usage: .eq target <-30 .. -6> (dBFS)");
                    break;
                }
                var next = _config.Update(s => s with { TargetDbFs = db, PresetName = "Custom" });
                _broker.PostDebugMessage(string.Format(CultureInfo.InvariantCulture,
                    "[AudioNormalizer] Target loudness {0:0.0} dBFS.", next.TargetDbFs));
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
                var t = _processor.GetAggregateTelemetry();
                _broker.PostDebugMessage(string.Format(CultureInfo.InvariantCulture,
                    "[AudioNormalizer] {0} preset={1} target={2:0.0}dBFS " +
                    "boost={3:0.0}dB cut={4:0.0}dB ratio={5:0.0}:1 gain={6:0.0}dB",
                    s.Enabled ? "ON" : "OFF", s.PresetName, s.TargetDbFs,
                    s.MaxBoostDb, s.MaxCutDb, s.CompressionRatio, t.CurrentGainDb));
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
        catch (Exception ex)
        {
            _broker.PostDebugMessage($"[AudioNormalizer] error: {ex.GetType().Name}: {ex.GetBaseException().Message}");
            return Task.CompletedTask;
        }
    }

    private Task ForwardToOriginal(string command, Func<string, Task>? resultHandler)
    {
        try
        {
            if (_forwardToViewModel is not null)
            {
                return _forwardToViewModel(command);
            }
            if (_originalDotCommandProcessor is not null)
            {
                var m = _originalDotCommandProcessor.GetType().GetMethod("Process", BindingFlags.Public | BindingFlags.Instance);
                var t = ReflectionInvoke.Invoke(m!, _originalDotCommandProcessor, command, resultHandler);
                if (t is Task task) return task;
            }
            if (resultHandler is not null)
            {
                return resultHandler(command);
            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _broker.PostDebugMessage($"[AudioNormalizer] Command error: {ex.GetBaseException().Message}");
            return Task.CompletedTask;
        }
    }

    private bool InstallMainTabWrapper()
    {
        // MainTabViewModel owns UI command routing: ProcessCommandLine(string)
        // consults its processCommandLineCommand delegate (IAsyncRelayCommand).
        // Swap that delegate's inner execute with our interceptor; keep the
        // original so non-.eq commands behave exactly as before. Also capture
        // the IDotCommandProcessor for the X-Plane + forward path.
        var mainTab = FindMainTabViewModel();
        if (mainTab is null)
        {
            return false;
        }
        _mainTabViewModel = mainTab;
        _originalProcessCommandLine = mainTab.GetType().GetMethod("ProcessCommandLine", BindingFlags.Public | BindingFlags.Instance);
        if (_originalProcessCommandLine is not null)
        {
            var vm = mainTab;
            var mi = _originalProcessCommandLine;
            _forwardToViewModel = cmd => (Task)ReflectionInvoke.Invoke(mi, vm, cmd)!;
        }

        // Best-effort: also wrap the IAsyncRelayCommand delegate so Enter-key
        // routing hits us even if ProcessCommandLine internals change shape.
        try
        {
            var cmdProp = mainTab.GetType().GetProperty("ProcessCommandLineCommand", BindingFlags.Public | BindingFlags.Instance);
            var relay = cmdProp?.GetValue(mainTab);
            var executeField = relay?.GetType().GetField("execute", BindingFlags.NonPublic | BindingFlags.Instance);
            var original = executeField?.GetValue(relay) as Delegate;
            if (original is not null && relay is not null && executeField is not null)
            {
                _originalCommandDelegate = original;
                var intercept = BuildExecuteInterceptor(original);
                if (intercept is not null)
                {
                    try { executeField.SetValue(relay, intercept); } catch { }
                }
            }
        }
        catch { }

        CaptureOriginalProcessor(mainTab);
        return _forwardToViewModel is not null || _originalDotCommandProcessor is not null;
    }

    private void CaptureOriginalProcessor(object mainTab)
    {
        try
        {
            var vmField = mainTab.GetType().GetField("_dotCommandProcessor", BindingFlags.NonPublic | BindingFlags.Instance);
            _originalDotCommandProcessor ??= vmField?.GetValue(mainTab);
        }
        catch { }
        try
        {
            var serviceProvider = FindServiceProvider();
            var spField = serviceProvider?.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType.Name.Contains("IDotCommandProcessor", StringComparison.Ordinal));
            _originalDotCommandProcessor ??= spField?.GetValue(serviceProvider);
        }
        catch { }
    }

    private Delegate? BuildExecuteInterceptor(Delegate original)
    {
        try
        {
            // CommunityToolkit IAsyncRelayCommand<string>.ExecuteAsync is
            // Func<string, Task>-shaped; build a same-signature wrapper so the
            // relay accepts it without knowing our type.
            var invoke = original.GetType().GetMethod("Invoke")!;
            var ps = invoke.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) return null;
            Func<string, Task> wrapper = cmd =>
            {
                var trimmed = (cmd ?? string.Empty).Trim();
                if (trimmed.Equals(".eq", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith(".eq ", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleCommand(trimmed, null);
                }
                // Same path as HandleCommand's forward: the captured
                // ProcessCommandLine delegate. Never DynamicInvoke — that API
                // is trimmed from the host runtime.
                if (_forwardToViewModel is not null)
                {
                    return _forwardToViewModel(cmd);
                }
                return Task.CompletedTask;
            };
            var targetType = original.GetType();
            if (targetType == typeof(Func<string, Task>)) return wrapper;
            try { return Delegate.CreateDelegate(targetType, wrapper, "Invoke"); }
            catch { return null; }
        }
        catch
        {
            return null;
        }
    }

    private object? FindMainTabViewModel()
    {
        try
        {
            // App.Current (static on Avalonia.Application base) -> ApplicationLifetime ->
            // MainWindow (Vatsim.Xpilot.Ui.Views.MainWindow) -> [field] MessagesAreaHost
            // (ContentPresenter) -> Content (MessagesAreaContent) -> DataContext
            // (MessagesAreaViewModel) -> [field] _mainTabViewModel.
            var app = GetAppCurrent();
            if (app is null)
            {
                return null;
            }
            var lifetime = GetPropertyValue(app, "ApplicationLifetime");
            var mainWindow = GetPropertyValue(lifetime, "MainWindow");
            if (mainWindow is null)
            {
                return null;
            }
            var host = GetFieldValue(mainWindow, "MessagesAreaHost");
            var content = GetPropertyValue(host, "Content") ?? host;
            var messagesVm = GetPropertyValue(content, "DataContext")
                ?? GetPropertyValue(mainWindow, "DataContext");
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
            var tabsProp = messagesVm.GetType().GetProperty("Tabs", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tabsProp?.GetValue(messagesVm) is IEnumerable tabs)
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

    private static object? GetAppCurrent()
    {
        try
        {
            var appType = FindType("Vatsim.Xpilot.App");
            // Current is declared on the Avalonia.Application base class.
            var currentProp = appType?.GetProperty("Current",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return currentProp?.GetValue(null);
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
            // Primary: App.Current._serviceProvider (Vatsim.Xpilot.App, private instance field).
            var app = GetAppCurrent();
            if (app is not null)
            {
                var f = app.GetType().GetField("_serviceProvider", BindingFlags.NonPublic | BindingFlags.Instance);
                var sp = f?.GetValue(app);
                if (sp is not null)
                {
                    return sp;
                }
            }
            // Fallback: scan loaded assemblies for Vatsim.Xpilot.Container.ServiceProvider
            // (Jab-generated container has no static Instance property).
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
            var busType = FindType("Vatsim.Xpilot.Events.EventBus.EventBus");
            var instanceProp = busType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var bus = instanceProp?.GetValue(null);
            var cmdType = FindType("Vatsim.Xpilot.Events.ProcessXplaneDotCommand");
            if (bus is null || cmdType is null || busType is null)
            {
                return;
            }

            // Cache the original private handler so non-.eq X-Plane commands keep
            // their exact original behavior after we take over the subscription.
            try
            {
                _originalOnXplaneDotCommand = _originalDotCommandProcessor?.GetType()
                    .GetMethod("OnProcessXplaneDotCommand", BindingFlags.NonPublic | BindingFlags.Instance);
                var subField = _originalDotCommandProcessor?.GetType()
                    .GetField("_processXplaneDotCommandSubscription", BindingFlags.NonPublic | BindingFlags.Instance);
                // Dispose immediately: the original subscribed first, so leaving it
                // installed would publish "Unknown command: .eq" before our handler
                // ever runs. Non-.eq commands are forwarded in OnXplaneDotCommand.
                (subField?.GetValue(_originalDotCommandProcessor) as IDisposable)?.Dispose();
            }
            catch { }

            Action<object> handler = OnXplaneDotCommand;
            _xplaneHandler = handler;
            var subscribe = busType.GetMethod("Subscribe", [typeof(Type), typeof(Action<object>)]);
            _eventBusXplaneSub = ReflectionInvoke.Invoke(subscribe!, bus, cmdType, handler) as IDisposable;
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
                // Preserve original behavior for every non-.eq X-Plane command
                // (dot commands via ProcessInternal, plain text via RadioMessageSent).
                try { ReflectionInvoke.Invoke(_originalOnXplaneDotCommand!, _originalDotCommandProcessor, msg); } catch { }
                return;
            }
            _ = HandleCommand(command, null);
        }
        catch { }
    }

    private static object? GetPropertyValue(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }
        try
        {
            return target.GetType().GetProperty(name, AnyInstance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetFieldValue(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }
        try
        {
            return target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
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
            // Re-subscribe the original X-Plane dot-command handler so X-Plane
            // text routing keeps working after the plugin unloads.
            if (_originalDotCommandProcessor is not null && _originalOnXplaneDotCommand is not null)
            {
                var busType = FindType("Vatsim.Xpilot.Events.EventBus.EventBus");
                var bus = busType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var cmdType = FindType("Vatsim.Xpilot.Events.ProcessXplaneDotCommand");
                var subscribe = busType?.GetMethod("Subscribe", [typeof(Type), typeof(Action<object>)]);
                if (bus is not null && cmdType is not null && subscribe is not null)
                {
                    var original = _originalDotCommandProcessor;
                    var method = _originalOnXplaneDotCommand;
                    Action<object> restore = msg =>
                    {
                        try { ReflectionInvoke.Invoke(method, original, msg); } catch { }
                    };
                    try { subscribe.Invoke(bus, [cmdType, restore]); } catch { }
                }
            }
            // Best-effort restore of the wrapped command delegate (same
            // non-public "execute" field the install patched).
            try
            {
                if (_mainTabViewModel is not null && _originalCommandDelegate is not null)
                {
                    var cmdProp = _mainTabViewModel.GetType().GetProperty("ProcessCommandLineCommand", BindingFlags.Public | BindingFlags.Instance);
                    var relay = cmdProp?.GetValue(_mainTabViewModel);
                    var executeField = relay?.GetType().GetField("execute", BindingFlags.NonPublic | BindingFlags.Instance);
                    try { executeField?.SetValue(relay, _originalCommandDelegate); } catch { }
                }
            }
            catch { }
        }
        catch { }
    }
}
