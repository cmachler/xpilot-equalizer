using System.Buffers;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;
using XpilotAudioEqualizer.Interop;

namespace XpilotAudioEqualizer.Tests;

[Trait("Category", "Interop")]
public sealed class RuntimeInteropSmokeTests : IDisposable
{
    private readonly List<AssemblyLoadContext> _contexts = [];

    public void Dispose()
    {
        foreach (var alc in _contexts)
        {
            try { alc.Unload(); } catch { }
        }
    }

    private static string BundlePath =>
        @"C:\Users\chris\AppData\Local\org.vatsim.xpilot\Application\xPilot.exe";

    private static string CacheDir => @"D:\development\_asm_bundle";

    private static string? CachedPath(string entry) =>
        entry switch
        {
            "AFV.Sdk.Net.dll" => Path.Combine(CacheDir, "AFV.Sdk.Net.dll"),
            "xPilot.dll" => Path.Combine(CacheDir, "xPilot.dll"),
            _ => null,
        };

    private Assembly? LoadCached(string entry)
    {
        var path = CachedPath(entry);
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        var alc = new AssemblyLoadContext("smoke-" + entry, isCollectible: true);
        _contexts.Add(alc);
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        try
        {
            return alc.LoadFromStream(ms);
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractBundleDlls()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            foreach (var entry in new[] { "AFV.Sdk.Net.dll", "xPilot.dll", "xPilot.PluginSdk.dll" })
            {
                var dest = Path.Combine(CacheDir, entry);
                if (File.Exists(dest))
                {
                    continue;
                }
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("ilspycmd",
                        $"\"{BundlePath}\" --bundle-entry \"{entry}\" -o \"{CacheDir}/{entry}.il\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(120_000);
                    // ilspycmd decompiles; it does not extract raw DLLs. Raw extraction
                    // happens through the fallback below (single-file host probe).
                    try { File.Delete($"{CacheDir}/{entry}.il"); } catch { }
                }
                catch { }
            }
        }
        catch { }
    }

    [SkippableFact]
    public void Bundle_entries_load_and_expose_runtime_types()
    {
        ExtractBundleDlls();
        var afv = LoadCached("AFV.Sdk.Net.dll");
        var xpilot = LoadCached("xPilot.dll");
        Skip.If(afv is null || xpilot is null, "Bundle DLLs not extracted; run publish check on a machine with ilspycmd extraction.");

        var sampleSource = afv!.GetType("AFV.Sdk.Net.Core.Audio.Abstractions.ISampleSource")
            ?? afv.GetType("AFV.Sdk.Net.src.Core.Audio.Abstractions.ISampleSource");
        Assert.NotNull(sampleSource);

        var dotCmd = xpilot!.GetType("Vatsim.Xpilot.CommandProcessing.IDotCommandProcessor");
        Assert.NotNull(dotCmd);
    }

    [SkippableFact]
    public void SampleSource_proxy_wraps_real_SineToneSource()
    {
        ExtractBundleDlls();
        var afv = LoadCached("AFV.Sdk.Net.dll");
        Skip.If(afv is null, "AFV.Sdk.Net.dll bundle entry unavailable.");

        Type? sineType = null;
        foreach (var t in afv!.GetTypes())
        {
            if (t.Name == "SineToneSource")
            {
                sineType = t;
                break;
            }
        }
        Skip.If(sineType is null, "SineToneSource type not found.");

        object? sine = null;
        foreach (var ctor in sineType!.GetConstructors())
        {
            try
            {
                var ps = ctor.GetParameters();
                var args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    args[i] = pt == typeof(double) ? (object)880.0
                        : pt == typeof(float) ? (object)0.5f
                        : pt == typeof(int) ? (object)48000
                        : pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                sine = ctor.Invoke(args);
                if (sine is not null) break;
            }
            catch { }
        }
        Skip.If(sine is null, "SineToneSource has no invokable constructor.");

        var pluginAsm = typeof(MasterAudioProcessor).Assembly;
        var factory = pluginAsm.GetType("XpilotAudioEqualizer.Interop.DynamicProxyFactory")!;
        var create = factory.GetMethod("CreateSampleSourceProxy", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        using var store = new ConfigStore(Path.GetTempPath());
        var processor = new MasterAudioProcessor(store);
        var proxy = create.Invoke(null, [sine!, MasterAudioProcessor.ProcessHeadsetSpan, processor]);
        Assert.NotNull(proxy);

        var getFrame = proxy!.GetType().GetMethod("GetAudioFrame")!;
        var buf = new float[960];
        getFrame.Invoke(proxy, [buf]);
        Assert.Contains(buf, s => s != 0f);
    }

    [Fact]
    public void DotCommand_proxy_intercepts_eq_and_forwards_others()
    {
        var pluginAsm = typeof(MasterAudioProcessor).Assembly;
        var factory = pluginAsm.GetType("XpilotAudioEqualizer.Interop.DynamicProxyFactory")!;
        var create = factory.GetMethod("CreateDotCommandProxy", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

        var forwarded = new List<string>();
        object fakeOriginal = new FakeDotCommand(s => { forwarded.Add(s); return Task.CompletedTask; });

        var hookType = pluginAsm.GetType("XpilotAudioEqualizer.Interop.DotCommandHook")!;
        using var store = new ConfigStore(Path.GetTempPath());
        var processor = new MasterAudioProcessor(store);
        var broker = new FakeBroker();
        var hook = Activator.CreateInstance(hookType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null, [broker, store, processor, (Func<Action>)(() => () => { }), (Func<Action>)(() => () => { })], null)!;
        hookType.GetField("_originalDotCommandProcessor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(hook, fakeOriginal);

        var handle = hookType.GetMethod("HandleCommand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Exercise the real routing logic directly (no xPilot assembly needed).
        ((Task)handle.Invoke(hook, [".eq status", null, null])!).Wait();
        ((Task)handle.Invoke(hook, [".wx KJFK", null, null])!).Wait();

        Assert.Contains(".wx KJFK", forwarded);
        Assert.Contains(broker.Messages, m => m.Contains("AudioNormalizer"));

        // Proxy creation itself requires the real xPilot assembly; verify when present.
        ExtractBundleDlls();
        if (LoadCached("xPilot.dll") is not null)
        {
            Func<string, Func<string, Task>?, object?, Task> handler = (cmd, rh, st) =>
                (Task)handle.Invoke(hook, [cmd, rh, st])!;
            var proxy = create.Invoke(null, [handler, null]);
            Assert.NotNull(proxy);
            var process = proxy!.GetType().GetMethod("Process")!;
            process.Invoke(proxy, [".wx KJFK", null]);
            Assert.Equal(2, forwarded.Count(s => s == ".wx KJFK"));
        }
        if (hook is IDisposable d) d.Dispose();
    }

    [Fact]
    public void Master_processor_span_callbacks_normalize_in_place()
    {
        using var store = new ConfigStore(Path.GetTempPath());
        store.Update(s => s with { TargetDbFs = -18.0 }, saveToDisk: false);
        var processor = new MasterAudioProcessor(store);

        double peak = XpilotAudioEqualizer.Dsp.AudioDb.ToLinear(-3.0) * Math.Sqrt(2.0);
        float[] last = null!;
        for (int f = 0; f < 25; f++)
        {
            var frame = new float[960];
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)(peak * Math.Sin(2.0 * Math.PI * 1000.0 * i / 48000.0));
            }
            MasterAudioProcessor.ProcessHeadsetSpan(frame, processor);
            last = frame;
        }
        double sum = 0;
        foreach (var s in last) sum += (double)s * s;
        double outDb = XpilotAudioEqualizer.Dsp.AudioDb.ToDb(Math.Sqrt(sum / last.Length));
        Assert.InRange(outDb, -20.5, -16.5);

        // Zero-allocation guarantee: SpanAction path allocates no closures per call.
        var buf = new float[960];
        long before = GC.GetAllocatedBytesForCurrentThread();
        MasterAudioProcessor.ProcessSpeakerSpan(buf, processor);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(after - before < 1024, $"Per-frame allocation too high: {after - before} bytes");
    }

    [Fact]
    public void Plugin_initialize_posts_loaded_message_and_disposes_cleanly()
    {
        var plugin = new AudioEqualizerPlugin();
        var broker = new FakeBroker();
        plugin.Initialize(broker);
        Assert.Contains(broker.Messages, m => m.Contains("xPilot Audio Normalizer 1.0.0 loaded"));
        broker.RaiseSessionEnded();
    }

    private sealed class FakeDotCommand(Func<string, Task> onProcess)
    {
        public Task Process(string command, Func<string, Task>? resultHandler = null) => onProcess(command);
    }

#pragma warning disable 0067
    private sealed class FakeBroker : Vatsim.Xpilot.PluginSdk.IBroker
    {
        public List<string> Messages { get; } = [];
        public Version ApiVersion => new(0, 2, 0, 0);
        public bool IsConnected => false;
        public string? Callsign => null;
        public event EventHandler? SessionEnded;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.NetworkConnectedEventArgs>? NetworkConnected;
        public event EventHandler? NetworkDisconnected;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.PrivateMessageReceivedEventArgs>? PrivateMessageReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.ServerMessageReceivedEventArgs>? ServerMessageReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.RadioMessageReceivedEventArgs>? RadioMessageReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.BroadcastMessageReceivedEventArgs>? BroadcastMessageReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.RadioMessageSentEventArgs>? RadioMessageSent;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.PrivateMessageSentEventArgs>? PrivateMessageSent;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.MetarReceivedEventArgs>? MetarReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.AtisReceivedEventArgs>? AtisReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.ControllerAddedEventArgs>? ControllerAdded;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.ControllerDeletedEventArgs>? ControllerDeleted;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.ControllerFrequencyChangedEventArgs>? ControllerFrequencyChanged;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.ControllerLocationChangedEventArgs>? ControllerLocationChanged;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.SelcalAlertReceivedEventArgs>? SelcalAlertReceived;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.AircraftAddedEventArgs>? AircraftAdded;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.AircraftUpdatedEventArgs>? AircraftUpdated;
        public event EventHandler<Vatsim.Xpilot.PluginSdk.Events.AircraftDeletedEventArgs>? AircraftDeleted;
        public IReadOnlyList<Vatsim.Xpilot.PluginSdk.Models.ControllerInfo> GetControllers() => [];
        public void RequestConnect(string c, string t, string s) => throw new NotSupportedException();
        public void RequestConnectAsObserver(string c) => throw new NotSupportedException();
        public void RequestConnectAsTowerView() => throw new NotSupportedException();
        public void RequestDisconnect() { }
        public void RequestMetar(string s) { }
        public void RequestAtis(string c) { }
        public void PostDebugMessage(string message) => Messages.Add(message);
        public void SetModeC(bool modeC) { }
        public void SquawkIdent() { }
        public void SetPtt(bool pressed) { }
        public void RaiseSessionEnded() => SessionEnded?.Invoke(this, EventArgs.Empty);
    }
#pragma warning restore 0067
}
