namespace XpilotAudioEqualizer.Dsp;

public sealed class NormalizerTelemetry
{
    public float InputRmsDb = float.NegativeInfinity;
    public float OutputRmsDb = float.NegativeInfinity;
    public float CurrentGainDb;
    public bool IsGateOpen;
    public long LastActiveTimestampTicks;

    public bool HasEverBeenWritten => LastActiveTimestampTicks != 0;

    // NOTE: plain field access, no Volatile. The host's trimmed runtime lacks
    // Volatile.Read/Write overloads for float/bool/long (MissingMethodException).
    // These are best-effort meters; a torn read is harmless.
    public void Write(float inputRmsDb, float outputRmsDb, float currentGainDb, bool gateOpen, long timestampTicks)
    {
        InputRmsDb = inputRmsDb;
        OutputRmsDb = outputRmsDb;
        CurrentGainDb = currentGainDb;
        IsGateOpen = gateOpen;
        LastActiveTimestampTicks = timestampTicks;
    }

    public (float InputRmsDb, float OutputRmsDb, float CurrentGainDb, bool IsGateOpen, long LastActiveTimestampTicks) Read() =>
        (InputRmsDb, OutputRmsDb, CurrentGainDb, IsGateOpen, LastActiveTimestampTicks);
}
