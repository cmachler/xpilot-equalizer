using Xunit;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;

namespace XpilotAudioEqualizer.Tests;

public sealed class ChannelNormalizerDspTests
{
    private const int FrameSamples = 960;
    private const double SampleRate = 48000.0;

    private static NormalizerSettings BaseSettings() => new NormalizerSettings
    {
        Enabled = true,
        PresetName = "Test",
        TargetDbFs = -18.0,
        MaxBoostDb = 18.0,
        MaxCutDb = 18.0,
        CompressionRatio = 4.0,
        AttackMs = 10.0,
        ReleaseMs = 350.0,
        GateOpenDb = -42.0,
        GateCloseDb = -47.0,
        NoiseFloorDb = -52.0,
        LimiterCeilingDb = -1.0,
    };

    private static float[] SineFrame(double rmsDb, double freqHz = 1000.0, int samples = FrameSamples)
    {
        // Sine RMS = peak / sqrt(2) -> peak = rms * sqrt(2).
        double peak = AudioDb.ToLinear(rmsDb) * Math.Sqrt(2.0);
        var frame = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            frame[i] = (float)(peak * Math.Sin(2.0 * Math.PI * freqHz * i / SampleRate));
        }
        return frame;
    }

    private static double RmsDb(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        return AudioDb.ToDb(Math.Sqrt(sum / frame.Length));
    }

    [Fact]
    public void Loud_transmission_is_attenuated_into_target_window()
    {
        var dsp = new ChannelNormalizerDsp();
        var settings = BaseSettings();
        float[] last = null!;
        for (int f = 0; f < 25; f++)
        {
            var frame = SineFrame(-3.0);
            dsp.ProcessChannel(frame.AsSpan(), 0, 1, settings);
            last = frame;
        }
        double outDb = RmsDb(last);
        Assert.InRange(outDb, -20.5, -16.5);

        double ceiling = AudioDb.ToLinear(settings.LimiterCeilingDb);
        foreach (var s in last)
        {
            Assert.True(Math.Abs(s) <= ceiling + 1e-4f);
        }
    }

    [Fact]
    public void Quiet_transmission_is_boosted_into_target_window()
    {
        var dsp = new ChannelNormalizerDsp();
        var settings = BaseSettings();
        float[] last = null!;
        for (int f = 0; f < 50; f++)
        {
            var frame = SineFrame(-34.0);
            dsp.ProcessChannel(frame.AsSpan(), 0, 1, settings);
            last = frame;
        }
        double outDb = RmsDb(last);
        Assert.InRange(outDb, -20.5, -16.5);
    }

    [Fact]
    public void Noise_gate_blocks_static_below_threshold()
    {
        var dsp = new ChannelNormalizerDsp();
        var settings = BaseSettings();
        var rng = new Random(42);
        for (int f = 0; f < 25; f++)
        {
            var frame = new float[FrameSamples];
            for (int i = 0; i < frame.Length; i++)
            {
                // White noise scaled to -48 dBFS RMS.
                frame[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * AudioDb.ToLinear(-48.0));
            }
            dsp.ProcessChannel(frame.AsSpan(), 0, 1, settings);
        }
        var t = dsp.Telemetry.Read();
        Assert.False(t.IsGateOpen);
        Assert.True(Math.Abs(t.CurrentGainDb) < 0.5);
    }

    [Fact]
    public void Brickwall_limiter_never_exceeds_ceiling_on_transient()
    {
        var dsp = new ChannelNormalizerDsp();
        var settings = BaseSettings();
        double ceiling = AudioDb.ToLinear(settings.LimiterCeilingDb);

        // Pre-boost gain with quiet tone.
        for (int f = 0; f < 30; f++)
        {
            var frame = SineFrame(-34.0);
            dsp.ProcessChannel(frame.AsSpan(), 0, 1, settings);
        }
        // Sudden 0 dBFS transient burst.
        var burst = new float[FrameSamples];
        Array.Fill(burst, 1.0f);
        dsp.ProcessChannel(burst.AsSpan(), 0, 1, settings);
        foreach (var s in burst)
        {
            Assert.True(Math.Abs(s) <= ceiling + 1e-4f);
        }
    }

    [Fact]
    public void Split_stereo_channels_normalize_independently()
    {
        var left = new ChannelNormalizerDsp();
        var right = new ChannelNormalizerDsp();
        var settings = BaseSettings();

        float[] lastLeft = null!;
        float[] lastRight = null!;
        for (int f = 0; f < 30; f++)
        {
            var loud = SineFrame(-3.0);
            var quiet = SineFrame(-32.0);
            var interleaved = new float[1920];
            for (int i = 0; i < 960; i++)
            {
                interleaved[i * 2] = loud[i];
                interleaved[i * 2 + 1] = quiet[i];
            }
            left.ProcessChannel(interleaved.AsSpan(), 0, 2, settings);
            right.ProcessChannel(interleaved.AsSpan(), 1, 2, settings);
            lastLeft = new float[960];
            lastRight = new float[960];
            for (int i = 0; i < 960; i++)
            {
                lastLeft[i] = interleaved[i * 2];
                lastRight[i] = interleaved[i * 2 + 1];
            }
        }

        var lt = left.Telemetry.Read();
        var rt = right.Telemetry.Read();
        Assert.True(lt.CurrentGainDb < 0, $"Left (loud) should attenuate, got {lt.CurrentGainDb}");
        Assert.True(rt.CurrentGainDb > 0, $"Right (quiet) should boost, got {rt.CurrentGainDb}");
        _ = (lastLeft, lastRight);
    }

    [Fact]
    public void Digital_silence_eases_gain_to_unity()
    {
        var dsp = new ChannelNormalizerDsp();
        var settings = BaseSettings();
        for (int f = 0; f < 10; f++)
        {
            var frame = SineFrame(-10.0);
            dsp.ProcessChannel(frame.AsSpan(), 0, 1, settings);
        }
        var silence = new float[FrameSamples];
        for (int f = 0; f < 40; f++)
        {
            dsp.ProcessChannel(silence.AsSpan(), 0, 1, settings);
        }
        var t = dsp.Telemetry.Read();
        Assert.True(Math.Abs(t.CurrentGainDb) < 1.0);
    }
}
