namespace LocalCast.Core;

public sealed record StreamSettings(int Height, int Fps, bool Adaptive)
{
    public static StreamSettings Default { get; } = new(1080, 30, true);
}
