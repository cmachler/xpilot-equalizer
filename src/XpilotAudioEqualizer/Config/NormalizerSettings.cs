namespace XpilotAudioEqualizer.Config;

public sealed record NormalizerSettings
{
    public bool Enabled { get; init; } = true;
    public string PresetName { get; init; } = "Balanced";
    public double TargetDbFs { get; init; } = -18.0;
    public double MaxBoostDb { get; init; } = 18.0;
    public double MaxCutDb { get; init; } = 18.0;
    public double CompressionRatio { get; init; } = 4.0;
    public double AttackMs { get; init; } = 10.0;
    public double ReleaseMs { get; init; } = 350.0;
    public double GateOpenDb { get; init; } = -42.0;
    public double GateCloseDb { get; init; } = -47.0;
    public double NoiseFloorDb { get; init; } = -52.0;
    public double LimiterCeilingDb { get; init; } = -1.0;

    public NormalizerSettings Clamp() => this with
    {
        TargetDbFs = Math.Clamp(TargetDbFs, -30.0, -6.0),
        MaxBoostDb = Math.Clamp(MaxBoostDb, 0.0, 24.0),
        MaxCutDb = Math.Clamp(MaxCutDb, 0.0, 24.0),
        CompressionRatio = Math.Clamp(CompressionRatio, 1.0, 10.0),
        AttackMs = Math.Clamp(AttackMs, 1.0, 100.0),
        ReleaseMs = Math.Clamp(ReleaseMs, 50.0, 1500.0),
        GateOpenDb = Math.Clamp(GateOpenDb, -60.0, -25.0),
        GateCloseDb = Math.Clamp(Math.Min(GateCloseDb, GateOpenDb - 3.0), -65.0, -28.0),
        NoiseFloorDb = Math.Clamp(NoiseFloorDb, -70.0, -35.0),
        LimiterCeilingDb = Math.Clamp(LimiterCeilingDb, -6.0, -0.1),
    };
}
