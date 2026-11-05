using System.Reflection;
using Vatsim.Xpilot.PluginSdk;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;
using XpilotAudioEqualizer.Interop;

namespace XpilotAudioEqualizer;

public sealed class AudioEqualizerPlugin : IPlugin
{
    public string Name => "xPilot Audio Normalizer 1.0.0";

    private ConfigStore? _config;
    private MasterAudioProcessor? _processor;
    private AudioPipelineHook? _audioHook;
    private DotCommandHook? _dotHook;
    private object? _window;
    private IBroker? _broker;
    private bool _sessionEnded;

    public void Initialize(IBroker broker)
    {
        try
        {
            if (broker.ApiVersion < new Version(0, 1))
            {
                return;
            }
        }
        catch
        {
            return;
        }

        _broker = broker;
        _config = new ConfigStore();
        _processor = new MasterAudioProcessor(_config);
        _audioHook = new AudioPipelineHook(broker, _processor);
        _dotHook = new DotCommandHook(broker, _config, _processor, ShowWindow, HideWindow);

        _audioHook.Install();
        _dotHook.Install();

        broker.SessionEnded += OnSessionEnded;

        try
        {
            var settings = _config.Current;
            broker.PostDebugMessage($"{Name} loaded (Target: {settings.TargetDbFs:0.0} dBFS). Type .eq to open settings.");
        }
        catch { }
    }

    private Action ShowWindow() => () => PostToUiThread(ShowWindowCore);

    private Action HideWindow() => () =>
    {
        try { PostToUiThread(HideWindowCore); }
        catch { }
    };

    private void PostToUiThread(Action action)
    {
        try
        {
            var dispatcherType = FindDispatcherType();
            if (dispatcherType is not null)
            {
                var uiThreadProp = dispatcherType.GetProperty("UIThread",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var uiThread = uiThreadProp?.GetValue(null);
                if (uiThread is not null)
                {
                    // Dispatcher.Post(Action, DispatcherPriority = default) has 2 parameters
                    // in metadata; request both overload shapes.
                    var post = uiThread.GetType().GetMethod("Post", [typeof(Action), typeof(int)])
                        ?? FindPostMethod(uiThread.GetType());
                    if (post is not null)
                    {
                        var args = post.GetParameters();
                        object?[] invokeArgs = args.Length == 1
                            ? [action]
                            : [action, DefaultPriorityValue(args[1].ParameterType)];
                        try
                        {
                            ReflectionInvoke.Invoke(post, uiThread, invokeArgs);
                            return;
                        }
                        catch
                        {
                            // DynamicMethod JIT can't resolve host-framework types
                            // (e.g. Avalonia DispatcherPriority) from our load
                            // context. The .eq wrapper already runs on the UI
                            // thread, so a direct call is correct; a background
                            // caller degrades gracefully via ShowWindowCore.
                        }
                    }
                }
            }
            action();
        }
        catch
        {
            _broker?.PostDebugMessage("[AudioNormalizer] Cannot open settings window on this Avalonia version; use .eq CLI commands.");
        }
    }

    private static System.Reflection.MethodInfo? FindPostMethod(Type uiThreadType)
    {
        foreach (var m in uiThreadType.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (((MemberInfo)m).Name != "Post")
            {
                continue;
            }
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(Action))
            {
                return m;
            }
        }
        return null;
    }

    private static object? DefaultPriorityValue(Type priorityType) =>
        priorityType.IsValueType ? Activator.CreateInstance(priorityType) : null;

    private void ShowWindowCore()
    {
        try
        {
            if (_config is null || _processor is null)
            {
                return;
            }
            _window ??= CreateWindowLate(_config, _processor);
            if (_window is null)
            {
                return;
            }
            ShowWindowLate(_window);
        }
        catch (Exception ex)
        {
            _broker?.PostDebugMessage($"[AudioNormalizer] Settings window unavailable: {ex.GetBaseException().Message}. Use .eq CLI commands.");
        }
    }

    private void HideWindowCore()
    {
        var w = _window;
        if (w is not null)
        {
            HideWindowLate(w);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object? CreateWindowLate(object config, object processor)
    {
        // Isolated so JIT never loads Avalonia assemblies until a window is shown.
        try { return new Ui.NormalizerWindow((Config.ConfigStore)config, (Dsp.MasterAudioProcessor)processor); }
        catch { return null; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ShowWindowLate(object w)
    {
        // Isolated so JIT never loads Avalonia assemblies until a window is shown.
        try
        {
            var win = (Ui.NormalizerWindow)w;
            if (!win.IsVisible) win.Show();
            else { try { win.Activate(); } catch { } }
        }
        catch { }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void HideWindowLate(object w)
    {
        try
        {
            var win = (Ui.NormalizerWindow)w;
            if (win.IsVisible) win.Hide();
        }
        catch { }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CloseWindowLate(object w)
    {
        // Isolated so JIT never loads Avalonia assemblies unless a window exists.
        try { ((Ui.NormalizerWindow)w).AllowDestroy(); } catch { }
        try { ((Ui.NormalizerWindow)w).Close(); } catch { }
    }

    private static Type? FindDispatcherType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
            {
                continue;
            }
            Type? t = null;
            try { t = asm.GetType("Avalonia.Threading.Dispatcher", throwOnError: false); } catch { }
            if (t is not null)
            {
                return t;
            }
        }
        return Type.GetType("Avalonia.Threading.Dispatcher, Avalonia.Base", throwOnError: false);
    }

    private void OnSessionEnded(object? sender, EventArgs e)
    {
        if (_sessionEnded)
        {
            return;
        }
        _sessionEnded = true;
        try { _dotHook?.Dispose(); } catch { }
        try { _audioHook?.Dispose(); } catch { }
        var w = _window;
        _window = null;
        if (w is not null)
        {
            CloseWindowLate(w);
        }
        try { _config?.Dispose(); } catch { }
    }
}
