namespace XpilotAudioEqualizer.Dsp;

public sealed class NormalizerTelemetry
{
    public float InputRmsDb;
    public float OutputRmsDb;
    public float CurrentGainDb;
    public bool IsGateOpen;
    public long LastActiveTimestampTicks;

    public void Write(float inputRmsDb, float outputRmsDb, float currentGainDb, bool gateOpen, long timestampTicks)
    {
        Volatile.Write(ref InputRmsDb, inputRmsDb);
        Volatile.Write(ref OutputRmsDb, outputRmsDb);
        Volatile.Write(ref CurrentGainDb, currentGainDb);
        Volatile.Write(ref IsGateOpen, gateOpen);
        Volatile.Write(ref LastActiveTimestampTicks, timestampTicks);
    }

    public (float InputRmsDb, float OutputRmsDb, float CurrentGainDb, bool IsGateOpen, long LastActiveTimestampTicks) Read() =>
        (Volatile.Read(ref InputRmsDb), Volatile.Read(ref OutputRmsDb), Volatile.Read(ref CurrentGainDb),
            Volatile.Read(ref IsGateOpen), Volatile.Read(ref LastActiveTimestampTicks));
}
