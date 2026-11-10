namespace LocalCast.Core.Protocol;

public static class ProtocolConstants
{
    public const string ServiceType = "_localcast._tcp";
    public const string ProtocolVersion = "1";
    public const int PinLength = 4;
    public const int MaxPinAttempts = 5;
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(2);
}
