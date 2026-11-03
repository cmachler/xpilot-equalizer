using XpilotAudioEqualizer.Config;

namespace XpilotAudioEqualizer.Dsp;

public sealed class ChannelNormalizerDsp
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;

    private const double DcAlpha = 0.99738;
    private const double SilenceThreshold = 1e-6;

    private double _dcPrevIn;
    private double _dcPrevOut;
    private double _currentGainLinear = 1.0;
    private double _envelopeLinear;
    private double _adaptiveNoiseFloor = AudioDb.ToLinear(-52.0);
    private bool _gateOpen;

    public NormalizerTelemetry Telemetry { get; } = new();

    public void Reset()
    {
        _dcPrevIn = 0;
        _dcPrevOut = 0;
        _currentGainLinear = 1.0;
        _envelopeLinear = 0;
        _adaptiveNoiseFloor = AudioDb.ToLinear(-52.0);
        _gateOpen = false;
    }

    public void ProcessChannel(Span<float> buffer, int channelOffset, int stride, NormalizerSettings settings)
    {
        settings = settings.Clamp();

        double gateOpenLinear = AudioDb.ToLinear(settings.GateOpenDb);
        double gateCloseLinear = AudioDb.ToLinear(settings.GateCloseDb);
        double noiseFloorMin = AudioDb.ToLinear(settings.NoiseFloorDb);
        double limiterCeiling = AudioDb.ToLinear(settings.LimiterCeilingDb);

        int n = channelOffset >= buffer.Length ? 0 : (buffer.Length - channelOffset + stride - 1) / stride;
        if (n <= 0)
        {
            return;
        }

        // Stage 1 — DC blocker + RMS/peak measurement (single pass, no allocation).
        double sumSq = 0;
        double peak = 0;
        double dcPrevIn = _dcPrevIn;
        double dcPrevOut = _dcPrevOut;
        for (int i = 0, idx = channelOffset; i < n; i++, idx += stride)
        {
            double x = buffer[idx];
            double y = x - dcPrevIn + DcAlpha * dcPrevOut;
            dcPrevIn = x;
            dcPrevOut = y;
            buffer[idx] = (float)y;
            sumSq += y * y;
            double a = Math.Abs(y);
            if (a > peak) peak = a;
        }
        _dcPrevIn = dcPrevIn;
        _dcPrevOut = dcPrevOut;

        double rms = Math.Sqrt(sumSq / n);

        float inputRmsDb = AudioDb.ToDbF(rms);

        if (peak < SilenceThreshold)
        {
            // Digital silence: ease gain back to unity, report silence, skip.
            double silenceEase = 1.0 - Math.Pow(0.95, 1000.0 * n / SampleRate / 20.0);
            _currentGainLinear += (1.0 - _currentGainLinear) * silenceEase;
            _gateOpen = false;
            Telemetry.Write(inputRmsDb, float.NegativeInfinity, AudioDb.ToDbF(_currentGainLinear), false, DateTime.UtcNow.Ticks);
            return;
        }

        // Stage 2 — adaptive noise floor + hysteresis gate.
        // Per-frame smoothing is scaled by frame duration so behavior is
        // invariant to buffer size (nominal frame = 960 samples @ 48 kHz).
        double frameMs = 1000.0 * n / SampleRate;
        double frameScale = frameMs / 20.0;
        if (rms < _adaptiveNoiseFloor)
        {
            _adaptiveNoiseFloor += (rms - _adaptiveNoiseFloor) * (1.0 - Math.Pow(0.80, frameScale));
        }
        else
        {
            _adaptiveNoiseFloor += (rms - _adaptiveNoiseFloor) * (1.0 - Math.Pow(0.9995, frameScale));
        }
        if (_adaptiveNoiseFloor < noiseFloorMin)
        {
            _adaptiveNoiseFloor = noiseFloorMin;
        }

        if (!_gateOpen && rms > gateOpenLinear && rms > 1.25 * _adaptiveNoiseFloor)
        {
            _gateOpen = true;
        }
        else if (_gateOpen && rms < gateCloseLinear)
        {
            _gateOpen = false;
        }

        if (_gateOpen)
        {
            double rise = 1.0 - Math.Pow(0.20, frameScale);   // ~15 ms across 20 ms frames
            double fall = 1.0 - Math.Pow(0.005, frameScale);  // ~120 ms across 20 ms frames
            double coeff = rms > _envelopeLinear ? rise : fall;
            _envelopeLinear += (rms - _envelopeLinear) * (1.0 - coeff);
        }

        // Stage 3 — upward/downward AGC + compressor target gain.
        double targetGainLinear = 1.0;
        if (_gateOpen)
        {
            double envelopeDb = AudioDb.ToDb(Math.Max(_envelopeLinear, 1e-7));
            double deltaDb = settings.TargetDbFs - envelopeDb;
            // Full AGC leveling far from target (guarantees convergence), ratio-shaped
            // gentle slope near target to avoid pumping/hunting on small deviations.
            double k = 1.0 - 1.0 / Math.Max(1.0, settings.CompressionRatio);
            double leveling = 0.75 + 0.25 * Math.Min(1.0, Math.Abs(deltaDb) / 8.0);
            double gainDb = Math.Clamp(deltaDb * Math.Max(k, leveling), -settings.MaxCutDb, settings.MaxBoostDb);
            targetGainLinear = AudioDb.ToLinear(gainDb);

            double transientHeadroomDb = settings.TargetDbFs + 6.0;
            double transientLimit = AudioDb.ToLinear(transientHeadroomDb);
            if (peak * targetGainLinear > limiterCeiling * 0.95)
            {
                targetGainLinear = limiterCeiling * 0.95 / Math.Max(peak, 1e-9);
            }
            else if (peak > transientLimit && targetGainLinear > 1.0)
            {
                targetGainLinear = Math.Min(targetGainLinear, transientLimit / Math.Max(peak, 1e-9));
            }
        }

        // Stage 4 — sample-accurate attack/release ballistics + soft-knee limiter.
        double attackCoeff = 1.0 - Math.Exp(-1.0 / (0.001 * settings.AttackMs * SampleRate));
        double releaseCoeff = 1.0 - Math.Exp(-1.0 / (0.001 * settings.ReleaseMs * SampleRate));
        double currentGain = _currentGainLinear;
        double ceiling = limiterCeiling;
        double knee = 0.75 * ceiling;

        double outSumSq = 0;
        for (int i = 0, idx = channelOffset; i < n; i++, idx += stride)
        {
            // Gate closed: always ease back toward unity at release rate so a
            // previously-boosted whisper fades out instead of snapping down at
            // attack rate. Gate open: attack for downward moves, release for up.
            double coeff = !_gateOpen ? releaseCoeff
                : targetGainLinear < currentGain ? attackCoeff : releaseCoeff;
            currentGain += (targetGainLinear - currentGain) * coeff;
            double s = buffer[idx] * currentGain;
            s = SoftLimit(s, ceiling, knee);
            buffer[idx] = (float)s;
            outSumSq += s * s;
        }
        _currentGainLinear = currentGain;

        float outputRmsDb = AudioDb.ToDbF(Math.Sqrt(outSumSq / n));
        Telemetry.Write(inputRmsDb, outputRmsDb, AudioDb.ToDbF(currentGain), _gateOpen, DateTime.UtcNow.Ticks);
    }

    private static double SoftLimit(double s, double ceiling, double knee)
    {
        double a = Math.Abs(s);
        if (a <= knee)
        {
            return s;
        }
        if (a >= ceiling)
        {
            return Math.CopySign(ceiling, s);
        }
        // C1-continuous cubic blend from linear (at knee) to ceiling.
        double t = (a - knee) / (ceiling - knee);
        double y = knee + (ceiling - knee) * (1.5 * t - 0.5 * t * t * t);
        return Math.CopySign(y, s);
    }

    internal double CurrentGainLinearForTest => _currentGainLinear;
    internal bool GateOpenForTest => _gateOpen;
}
