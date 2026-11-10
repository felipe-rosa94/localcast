using LocalCast.Core.Protocol;

namespace LocalCast.Core.Discovery;

public record DiscoveredTv(
    string Id,
    string Name,
    string IpAddress,
    int Port,
    TvState State,
    DateTime LastSeen
)
{
    public string DisplayText => $"{Name} ({IpAddress}:{Port})";
    public bool IsAvailable => State == TvState.Available;
}
