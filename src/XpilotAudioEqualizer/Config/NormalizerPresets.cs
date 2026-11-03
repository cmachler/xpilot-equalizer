namespace XpilotAudioEqualizer.Config;

public static class NormalizerPresets
{
    public const string Balanced = "Balanced";
    public const string Aggressive = "Aggressive Leveler";
    public const string Gentle = "Gentle / Natural";
    public const string Night = "Night Mode (Quiet Ceiling)";

    public static IReadOnlyList<string> Names { get; } =
        [Balanced, Aggressive, Gentle, Night];

    public static NormalizerSettings Get(string name) => name switch
    {
        Aggressive => new NormalizerSettings
        {
            Enabled = true,
            PresetName = Aggressive,
            TargetDbFs = -15.0,
            MaxBoostDb = 22.0,
            MaxCutDb = 22.0,
            CompressionRatio = 8.0,
            AttackMs = 5.0,
            ReleaseMs = 250.0,
            GateOpenDb = -40.0,
            GateCloseDb = -45.0,
            NoiseFloorDb = -52.0,
            LimiterCeilingDb = -1.0,
        },
        Gentle => new NormalizerSettings
        {
            Enabled = true,
            PresetName = Gentle,
            TargetDbFs = -20.0,
            MaxBoostDb = 12.0,
            MaxCutDb = 12.0,
            CompressionRatio = 2.5,
            AttackMs = 20.0,
            ReleaseMs = 500.0,
            GateOpenDb = -45.0,
            GateCloseDb = -50.0,
            NoiseFloorDb = -52.0,
            LimiterCeilingDb = -1.0,
        },
        Night => new NormalizerSettings
        {
            Enabled = true,
            PresetName = Night,
            TargetDbFs = -22.0,
            MaxBoostDb = 16.0,
            MaxCutDb = 24.0,
            CompressionRatio = 6.0,
            AttackMs = 4.0,
            ReleaseMs = 300.0,
            GateOpenDb = -42.0,
            GateCloseDb = -47.0,
            NoiseFloorDb = -52.0,
            LimiterCeilingDb = -3.0,
        },
        _ => new NormalizerSettings { PresetName = Balanced },
    };

    public static bool TryGet(string name, out NormalizerSettings settings)
    {
        foreach (var n in Names)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                settings = Get(n);
                return true;
            }
        }
        settings = Get(Balanced);
        return false;
    }
}
