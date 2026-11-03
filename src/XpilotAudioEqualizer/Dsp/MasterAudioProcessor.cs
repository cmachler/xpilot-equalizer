using System.Buffers;
using XpilotAudioEqualizer.Config;

namespace XpilotAudioEqualizer.Dsp;

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

    public (float InputRmsDb, float OutputRmsDb, float CurrentGainDb, bool IsGateOpen) GetAggregateTelemetry(bool headset)
    {
        var a = (headset ? _headsetLeft : _speakerLeft).Telemetry.Read();
        var b = (headset ? _headsetRight : _speakerRight).Telemetry.Read();
        return (
            MathF.Max(a.InputRmsDb, b.InputRmsDb),
            MathF.Max(a.OutputRmsDb, b.OutputRmsDb),
            MathF.Max(a.CurrentGainDb, b.CurrentGainDb),
            a.IsGateOpen || b.IsGateOpen);
    }
}
