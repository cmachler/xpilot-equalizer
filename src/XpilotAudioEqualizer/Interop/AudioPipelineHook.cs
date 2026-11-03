using System.Buffers;
using System.Reflection;
using Vatsim.Xpilot.PluginSdk;
using XpilotAudioEqualizer.Dsp;

namespace XpilotAudioEqualizer.Interop;

internal sealed class AudioPipelineHook : IDisposable
{
    private readonly IBroker _broker;
    private readonly MasterAudioProcessor _processor;
    private bool _disposed;

    private object? _voiceManager;
    private object? _afvClient;
    private object? _audioManager;
    private object? _originalHeadsetSource;
    private object? _originalSpeakerSource;
    private object? _headsetProxy;
    private object? _speakerProxy;
    private EventHandler<Vatsim.Xpilot.PluginSdk.Events.NetworkConnectedEventArgs>? _networkConnectedHandler;
    private EventHandler? _networkDisconnectedHandler;
    private Delegate? _voiceConnectedHandler;
    private Delegate? _voiceDisconnectedHandler;
    private bool _installed;

    public AudioPipelineHook(IBroker broker, MasterAudioProcessor processor)
    {
        _broker = broker;
        _processor = processor;
    }

    public bool Install()
    {
        try
        {
            if (!ResolveChain())
            {
                _broker.PostDebugMessage("[AudioNormalizer] Audio hook unavailable: voice pipeline not found.");
                return false;
            }
            InstallProxies();
            SubscribeReconnect();
            _installed = true;
            return true;
        }
        catch (Exception ex)
        {
            _broker.PostDebugMessage($"[AudioNormalizer] Audio hook failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void EnsureInstalled()
    {
        if (_disposed || !_installed)
        {
            return;
        }
        try
        {
            if (_audioManager is null)
            {
                if (!ResolveChain())
                {
                    return;
                }
            }
            var headsetField = FindFieldByType(_audioManager!.GetType(), "ISampleSource", "_headsetSource")
                ?? FindFieldByType(_audioManager.GetType(), "ISampleSource", "_headset");
            var speakerField = FindFieldByType(_audioManager.GetType(), "ISampleSource", "_speakerSource")
                ?? FindFieldByType(_audioManager.GetType(), "ISampleSource", "_speaker");
            if (headsetField is null || speakerField is null)
            {
                return;
            }
            var curHeadset = headsetField.GetValue(_audioManager);
            var curSpeaker = speakerField.GetValue(_audioManager);
            if (!ReferenceEquals(curHeadset, _headsetProxy) || !ReferenceEquals(curSpeaker, _speakerProxy))
            {
                InstallProxies();
            }
        }
        catch
        {
            // Never crash the host from a maintenance check.
        }
    }

    private bool ResolveChain()
    {
        // broker (PluginBroker) -> _voiceManager -> _afvClient -> _audioManager
        _voiceManager = GetFieldValue(_broker, "_voiceManager")
            ?? ScanFieldByTypeName(_broker, "IVoiceManager")
            ?? ScanFieldByTypeName(_broker, "VoiceManager");
        if (_voiceManager is null)
        {
            return false;
        }
        _afvClient = GetFieldValue(_voiceManager, "_afvClient")
            ?? ScanFieldByTypeName(_voiceManager, "AfvClient");
        if (_afvClient is null)
        {
            return false;
        }
        _audioManager = GetFieldValue(_afvClient, "_audioManager")
            ?? ScanFieldByTypeName(_afvClient, "MiniAudioDeviceManager");
        return _audioManager is not null;
    }

    private void InstallProxies()
    {
        var mgrType = _audioManager!.GetType();
        var headsetField = FindFieldByType(mgrType, "ISampleSource", "_headsetSource")
            ?? FindFieldByType(mgrType, "ISampleSource", "_headset");
        var speakerField = FindFieldByType(mgrType, "ISampleSource", "_speakerSource")
            ?? FindFieldByType(mgrType, "ISampleSource", "_speaker");
        if (headsetField is null || speakerField is null)
        {
            throw new InvalidOperationException("MiniAudioDeviceManager source fields not found.");
        }

        var setHeadset = mgrType.GetMethod("SetHeadsetSource", BindingFlags.Public | BindingFlags.Instance);
        var setSpeaker = mgrType.GetMethod("SetSpeakerSource", BindingFlags.Public | BindingFlags.Instance);
        if (setHeadset is null || setSpeaker is null)
        {
            throw new InvalidOperationException("SetHeadsetSource/SetSpeakerSource not found.");
        }

        if (_originalHeadsetSource is null)
        {
            _originalHeadsetSource = headsetField.GetValue(_audioManager);
        }
        if (_originalSpeakerSource is null)
        {
            _originalSpeakerSource = speakerField.GetValue(_audioManager);
        }

        // Wrap the live sources (OutputAudioDevice instances). If already ours, reuse.
        var liveHeadset = headsetField.GetValue(_audioManager);
        var liveSpeaker = speakerField.GetValue(_audioManager);
        if (!ReferenceEquals(liveHeadset, _headsetProxy) && liveHeadset is not null)
        {
            _headsetProxy = DynamicProxyFactory.CreateSampleSourceProxy(
                liveHeadset, MasterAudioProcessor.ProcessHeadsetSpan, _processor);
        }
        else if (_headsetProxy is null && liveHeadset is not null)
        {
            _headsetProxy = DynamicProxyFactory.CreateSampleSourceProxy(
                liveHeadset, MasterAudioProcessor.ProcessHeadsetSpan, _processor);
        }
        if (!ReferenceEquals(liveSpeaker, _speakerProxy) && liveSpeaker is not null)
        {
            _speakerProxy = DynamicProxyFactory.CreateSampleSourceProxy(
                liveSpeaker, MasterAudioProcessor.ProcessSpeakerSpan, _processor);
        }
        else if (_speakerProxy is null && liveSpeaker is not null)
        {
            _speakerProxy = DynamicProxyFactory.CreateSampleSourceProxy(
                liveSpeaker, MasterAudioProcessor.ProcessSpeakerSpan, _processor);
        }

        if (_headsetProxy is not null)
        {
            setHeadset.Invoke(_audioManager, [_headsetProxy]);
        }
        if (_speakerProxy is not null)
        {
            setSpeaker.Invoke(_audioManager, [_speakerProxy]);
        }
    }

    private void SubscribeReconnect()
    {
        _networkConnectedHandler = (_, _) => EnsureInstalled();
        _networkDisconnectedHandler = (_, _) => EnsureInstalled();
        _broker.NetworkConnected += _networkConnectedHandler;
        _broker.NetworkDisconnected += _networkDisconnectedHandler;

        // AfvClient.VoiceServerConnected / VoiceServerDisconnected are generic
        // EventHandler<TEventArgs>; passing plain EventHandler throws
        // ArgumentException. Build a compatible EventHandler<T> per event.
        if (_afvClient is not null)
        {
            foreach (var eventName in new[] { "VoiceServerConnected", "VoiceServerDisconnected" })
            {
                try
                {
                    var ev = _afvClient.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
                    if (ev is null)
                    {
                        continue;
                    }
                    var handler = CreateCompatibleHandler(ev.EventHandlerType!);
                    if (handler is null)
                    {
                        continue;
                    }
                    ev.AddEventHandler(_afvClient, handler);
                    if (eventName == "VoiceServerConnected") _voiceConnectedHandler = handler;
                    else _voiceDisconnectedHandler = handler;
                }
                catch { }
            }
        }
    }

    private Delegate? CreateCompatibleHandler(Type handlerType)
    {
        try
        {
            // EventHandler<TEventArgs>: bind OnAnyVoiceEvent<TEventArgs>.
            if (handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>))
            {
                var arg = handlerType.GetGenericArguments()[0];
                var method = typeof(AudioPipelineHook).GetMethod(nameof(OnAnyVoiceEvent),
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                return method.MakeGenericMethod(arg).CreateDelegate(handlerType, this);
            }
            if (handlerType == typeof(EventHandler))
            {
                return new EventHandler((_, _) => EnsureInstalled());
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void OnAnyVoiceEvent<T>(object? sender, T e) => EnsureInstalled();

    private static object? GetFieldValue(object target, string name)
    {
        var f = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(target);
    }

    private static object? ScanFieldByTypeName(object target, string typeNameContains)
    {
        foreach (var f in target.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                if (f.FieldType.Name.Contains(typeNameContains, StringComparison.Ordinal))
                {
                    return f.GetValue(target);
                }
            }
            catch { }
        }
        return null;
    }

    private static FieldInfo? FindFieldByType(Type owner, string fieldTypeName, string? nameHint)
    {
        FieldInfo? fallback = null;
        foreach (var f in owner.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (!f.FieldType.Name.Contains(fieldTypeName, StringComparison.Ordinal))
            {
                continue;
            }
            if (nameHint is not null && f.Name.Contains(nameHint.TrimStart('_'), StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
            fallback ??= f;
        }
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (_networkConnectedHandler is not null) _broker.NetworkConnected -= _networkConnectedHandler;
            if (_networkDisconnectedHandler is not null) _broker.NetworkDisconnected -= _networkDisconnectedHandler;
            if (_afvClient is not null)
            {
                var t = _afvClient.GetType();
                try
                {
                    if (_voiceConnectedHandler is not null)
                        t.GetEvent("VoiceServerConnected")?.RemoveEventHandler(_afvClient, _voiceConnectedHandler);
                    if (_voiceDisconnectedHandler is not null)
                        t.GetEvent("VoiceServerDisconnected")?.RemoveEventHandler(_afvClient, _voiceDisconnectedHandler);
                }
                catch { }
            }
            if (_audioManager is not null && (_originalHeadsetSource is not null || _originalSpeakerSource is not null))
            {
                var mgrType = _audioManager.GetType();
                try
                {
                    if (_originalHeadsetSource is not null)
                        mgrType.GetMethod("SetHeadsetSource")?.Invoke(_audioManager, [_originalHeadsetSource]);
                    if (_originalSpeakerSource is not null)
                        mgrType.GetMethod("SetSpeakerSource")?.Invoke(_audioManager, [_originalSpeakerSource]);
                }
                catch { }
            }
        }
        catch { }
    }
}
