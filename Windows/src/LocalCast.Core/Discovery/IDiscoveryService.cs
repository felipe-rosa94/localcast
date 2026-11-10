namespace LocalCast.Core.Discovery;

public interface IDiscoveryService : IDisposable
{
    event Action<DiscoveredTv>? TvDiscovered;
    event Action<string>? TvLost;

    IReadOnlyCollection<DiscoveredTv> DiscoveredTvs { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task RefreshAsync();
}
