using System.Buffers;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Xunit;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;
using XpilotAudioEqualizer.Interop;

namespace XpilotAudioEqualizer.Tests;

[Trait("Category", "Interop")]
public sealed class RuntimeInteropSmokeTests
{
    private static string BundlePath =>
        @"C:\Users\chris\AppData\Local\org.vatsim.xpilot\Application\xPilot.exe";

    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "xpilot-equalizer-smoke");

    private static string? CachedPath(string entry) =>
        entry switch
        {
            "AFV.Sdk.Net.dll" => Path.Combine(CacheDir, "AFV.Sdk.Net.dll"),
            "xPilot.dll" => Path.Combine(CacheDir, "xPilot.dll"),
            _ => null,
        };

    private static string UniqueTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xpilot-eq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Assembly? LoadIntoDefault(string entry)
    {
        var path = CachedPath(entry);
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        // DynamicProxyFactory emits a non-collectible run-only assembly, so the
        // AFV/xPilot reference assemblies must live in a non-collectible context
        // too. The default ALC satisfies that.
        AssemblyLoadContext.Default.Resolving += ResolveFromCache;
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static Assembly? ResolveFromCache(AssemblyLoadContext ctx, AssemblyName name)
    {
        try
        {
            var candidate = Path.Combine(CacheDir, (name.Name ?? string.Empty) + ".dll");
            if (File.Exists(candidate))
            {
                return ctx.LoadFromAssemblyPath(candidate);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts entries from the xPilot single-file bundle by parsing the
    /// ".net core bundle" footer directly. Previous version shelled out to
    /// ilspycmd (not on PATH), wrote .il files, then deleted them — always
    /// skipping these tests and creating D:\development\_asm_bundle.
    /// </summary>
    private static void ExtractBundleDlls()
    {
        try
        {
            var wanted = new[] { "AFV.Sdk.Net.dll", "xPilot.dll" };
            if (wanted.All(e => File.Exists(CachedPath(e)!)))
            {
                return;
            }
            if (!File.Exists(BundlePath))
            {
                return;
            }
            Directory.CreateDirectory(CacheDir);
            using var fs = File.OpenRead(BundlePath);
            using var br = new BinaryReader(fs);
            // Bundle footer: headerOffset (int64) immediately followed by the
            // 32-byte ".net core bundle" signature. The signature is NOT at EOF;
            // the bundle header + string table live after it in the file (here at
            // ~8 MB in a ~30 MB exe), so scan the whole file.
            ReadOnlySpan<byte> signature =
            [
                0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
                0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
                0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
            ];
            byte[] all = File.ReadAllBytes(BundlePath);
            long footerOffset = -1;
            for (int i = all.Length - signature.Length - 8; i >= 0; i--)
            {
                if (all[i + 8] != signature[0])
                {
                    continue;
                }
                bool match = true;
                for (int j = 0; j < signature.Length; j++)
                {
                    if (all[i + 8 + j] != signature[j]) { match = false; break; }
                }
                if (match)
                {
                    footerOffset = i;
                    break;
                }
            }
            if (footerOffset < 0)
            {
                return;
            }
            fs.Seek(footerOffset, SeekOrigin.Begin);
            long headerOffset = br.ReadInt64();
            fs.Seek(headerOffset, SeekOrigin.Begin);
            uint major = br.ReadUInt32();
            _ = br.ReadUInt32(); // minor
            uint numFiles = br.ReadUInt32();
            if (major is < 2 or > 6)
            {
                return;
            }
            ReadBundleString(br); // bundle id
            if (major >= 2)
            {
                fs.Seek(40, SeekOrigin.Current); // deps(16) + runtimeconfig(16) + flags(8)
            }
            for (uint i = 0; i < numFiles; i++)
            {
                long offset = br.ReadInt64();
                long size = br.ReadInt64();
                long compressedSize = br.ReadInt64();
                byte type = br.ReadByte();
                string relPath = ReadBundleString(br);
                string fileName = Path.GetFileName(relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!wanted.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                long pos = fs.Position;
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] raw = br.ReadBytes((int)(type == 1 && compressedSize > 0 ? compressedSize : size));
                if (type == 1 && compressedSize > 0 && compressedSize != size)
                {
                    using var ms = new MemoryStream(raw, writable: false);
                    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream((int)size);
                    ds.CopyTo(outMs);
                    raw = outMs.ToArray();
                }
                var dest = CachedPath(fileName)!;
                bool stale = !File.Exists(dest) || new FileInfo(dest).Length != raw.Length;
                if (stale)
                {
                    File.WriteAllBytes(dest, raw);
                }
                fs.Seek(pos, SeekOrigin.Begin);
            }
        }
        catch { }
    }

    private static string ReadBundleString(BinaryReader br)
    {
        int length = 0;
        int shift = 0;
        byte b;
        do
        {
            b = br.ReadByte();
            length |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return System.Text.Encoding.UTF8.GetString(br.ReadBytes(length));
    }

    private static Type[] GetTypesSafe(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // AFV.Sdk.Net.dll is trimmed; async state machines reference
            // trimmed members. Usable types still load.
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    [SkippableFact]
    public void Bundle_entries_load_and_expose_runtime_types()
    {
        ExtractBundleDlls();
        var afv = LoadIntoDefault("AFV.Sdk.Net.dll");
        var xpilot = LoadIntoDefault("xPilot.dll");
        Skip.If(afv is null || xpilot is null, "xPilot bundle DLLs unavailable on this machine.");

        var sampleSource = afv!.GetType("AFV.Sdk.Net.Core.Audio.Abstractions.ISampleSource");
        Assert.NotNull(sampleSource);

        var dotCmd = xpilot!.GetType("Vatsim.Xpilot.CommandProcessing.IDotCommandProcessor");
        Assert.NotNull(dotCmd);
    }

    [SkippableFact]
    public void SampleSource_proxy_wraps_real_SineToneSource()
    {
        ExtractBundleDlls();
        var afv = LoadIntoDefault("AFV.Sdk.Net.dll");
        Skip.If(afv is null, "AFV.Sdk.Net.dll bundle entry unavailable.");

        Type? sineType = null;
        foreach (var t in GetTypesSafe(afv!))
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

        using var store = new ConfigStore(UniqueTempDir());
        var processor = new MasterAudioProcessor(store);
        var proxy = DynamicProxyFactory.CreateSampleSourceProxy(
            sine!, MasterAudioProcessor.ProcessHeadsetSpan, processor);
        Assert.NotNull(proxy);

        // Span<float> is byref-like and cannot be boxed into MethodInfo.Invoke
        // args; emit a tiny DynamicMethod that calls GetAudioFrame directly.
        var getFrame = proxy!.GetType().GetMethod("GetAudioFrame")!;
        var callGetFrame = new DynamicMethod("CallGetFrame", typeof(void),
            [typeof(object), typeof(float[])], typeof(RuntimeInteropSmokeTests).Module, skipVisibility: true);
        var il = callGetFrame.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, proxy.GetType());
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Newobj, typeof(Span<float>).GetConstructor([typeof(float[])])!);
        il.Emit(OpCodes.Callvirt, getFrame);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        var invoker = (Action<object, float[]>)callGetFrame.CreateDelegate(typeof(Action<object, float[]>));
        var buf = new float[960];
        invoker(proxy, buf);
        Assert.Contains(buf, s => s != 0f);
    }

    [Fact]
    public async Task DotCommand_proxy_intercepts_eq_and_forwards_others()
    {
        var forwarded = new List<string>();
        object fakeOriginal = new FakeDotCommand(s => { forwarded.Add(s); return Task.CompletedTask; });

        using var store = new ConfigStore(UniqueTempDir());
        var processor = new MasterAudioProcessor(store);
        var broker = new FakeBroker();
        var hook = new DotCommandHook(broker, store, processor,
            (Func<Action>)(() => () => { }), (Func<Action>)(() => () => { }));
        typeof(DotCommandHook).GetField("_originalDotCommandProcessor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(hook, fakeOriginal);

        // Exercise the real routing logic directly (no xPilot assembly needed).
        await hook.HandleCommand(".eq status", null);
        await hook.HandleCommand(".wx KJFK", null);

        Assert.Contains(".wx KJFK", forwarded);
        Assert.Contains(broker.Messages, m => m.Contains("AudioNormalizer"));

        // Proxy creation itself requires the real xPilot assembly; verify when present.
        ExtractBundleDlls();
        if (LoadIntoDefault("xPilot.dll") is not null)
        {
            Func<string, Func<string, Task>?, object?, Task> handler = (cmd, rh, st) =>
                hook.HandleCommand(cmd, rh);
            var proxy = DynamicProxyFactory.CreateDotCommandProxy(handler, null);
            Assert.NotNull(proxy);
            var process = proxy!.GetType().GetMethod("Process")!;
            process.Invoke(proxy, [".wx KJFK", null]);
            Assert.Equal(2, forwarded.Count(s => s == ".wx KJFK"));
        }
        hook.Dispose();
    }

    [Fact]
    public void Master_processor_span_callbacks_normalize_in_place()
    {
        using var store = new ConfigStore(UniqueTempDir());
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
