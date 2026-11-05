using XpilotAudioEqualizer.Config;

namespace XpilotAudioEqualizer.Dsp;

/// <summary>
/// Local SpanAction equivalent. System.Buffers.Binary lives only in the
/// System.Buffers facade (trimmed from xPilot's bundle); the delegate shape
/// itself is trivial, so define it here to avoid the dependency.
/// </summary>
public delegate void SpanAction<T, TArg>(Span<T> span, TArg arg);

public sealed class MasterAudioProcessor
{
    private readonly ConfigStore _config;
    private readonly ChannelNormalizerDsp _headsetLeft = new();
    private readonly ChannelNormalizerDsp _headsetRight = new();
    private readonly ChannelNormalizerDsp _speakerLeft = new();
    private readonly ChannelNormalizerDsp _speakerRight = new();

    public static readonly SpanAction<float, object> ProcessHeadsetSpan = static (buffer, state) =>
    {
        if (state is MasterAudioProcessor p)
        {
            p.ProcessHeadset(buffer);
        }
    };

    public static readonly SpanAction<float, object> ProcessSpeakerSpan = static (buffer, state) =>
    {
        if (state is MasterAudioProcessor p)
        {
            p.ProcessSpeaker(buffer);
        }
    };

    public MasterAudioProcessor(ConfigStore config)
    {
        _config = config;
    }

    public void ProcessHeadset(Span<float> buffer)
    {
        var settings = _config.Current;
        if (!settings.Enabled || buffer.IsEmpty)
        {
            return;
        }
        if (buffer.Length == 1920)
        {
            // Split stereo: COM1 left, COM2 right (960 frames per channel).
            _headsetLeft.ProcessChannel(buffer, 0, 2, settings);
            _headsetRight.ProcessChannel(buffer, 1, 2, settings);
        }
        else
        {
            _headsetLeft.ProcessChannel(buffer, 0, 1, settings);
        }
    }

    public void ProcessSpeaker(Span<float> buffer)
    {
        var settings = _config.Current;
        if (!settings.Enabled || buffer.IsEmpty)
        {
            return;
        }
        if (buffer.Length == 1920)
        {
            _speakerLeft.ProcessChannel(buffer, 0, 2, settings);
            _speakerRight.ProcessChannel(buffer, 1, 2, settings);
        }
        else
        {
            _speakerLeft.ProcessChannel(buffer, 0, 1, settings);
        }
    }

    /// <summary>
    /// Aggregate across the active bus. Before the fix this used MathF.Max over
    /// all four channels; inactive stereo-right DSPs sit at 0.0 which pins the
    /// meters at 0 dBFS and masks attenuation. Now only channels with a
    /// timestamp are aggregated, and gain keeps its sign (loudest abs wins).
    /// </summary>
    public (float InputRmsDb, float OutputRmsDb, float CurrentGainDb, bool IsGateOpen) GetAggregateTelemetry(bool? headset = null)
    {
        var channels = new[]
        {
            (headset: true, T: _headsetLeft.Telemetry.Read()),
            (headset: true, T: _headsetRight.Telemetry.Read()),
            (headset: false, T: _speakerLeft.Telemetry.Read()),
            (headset: false, T: _speakerRight.Telemetry.Read()),
        };
        float inDb = float.NegativeInfinity;
        float outDb = float.NegativeInfinity;
        float gainDb = 0f;
        float maxAbsGain = -1f;
        bool gateOpen = false;
        bool any = false;
        foreach (var ch in channels)
        {
            if (headset.HasValue && ch.headset != headset.Value)
            {
                continue;
            }
            var t = ch.T;
            if (t.LastActiveTimestampTicks == 0)
            {
                continue;
            }
            any = true;
            inDb = MathF.Max(inDb, t.InputRmsDb);
            outDb = MathF.Max(outDb, t.OutputRmsDb);
            float abs = MathF.Abs(t.CurrentGainDb);
            if (abs > maxAbsGain)
            {
                maxAbsGain = abs;
                gainDb = t.CurrentGainDb;
            }
            gateOpen |= t.IsGateOpen;
        }
        return any ? (inDb, outDb, gainDb, gateOpen) : (float.NegativeInfinity, float.NegativeInfinity, 0f, false);
    }
}
