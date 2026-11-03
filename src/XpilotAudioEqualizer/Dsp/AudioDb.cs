namespace XpilotAudioEqualizer.Dsp;

public static class AudioDb
{
    public const double MinDb = -90.0;
    private const double MinLinear = 1e-7;

    public static double ToLinear(double db) => Math.Pow(10.0, db / 20.0);

    public static float ToLinearF(double db) => (float)Math.Pow(10.0, db / 20.0);

    public static double ToDb(double linear) =>
        linear <= MinLinear ? MinDb : 20.0 * Math.Log10(linear);

    public static float ToDbF(double linear) =>
        linear <= MinLinear ? (float)MinDb : (float)(20.0 * Math.Log10(linear));
}
