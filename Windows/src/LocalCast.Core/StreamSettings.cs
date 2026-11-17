namespace LocalCast.Core;

public sealed record StreamSettings(int Height, int Fps, bool Adaptive)
{
    public static StreamSettings Default { get; } = new(1080, 30, true);
    public static StreamSettings Light { get; } = new(720, 30, true);

    public const int DefaultBitrateKbps = 10000;
    public const int LightBitrateKbps = 4000;
}
