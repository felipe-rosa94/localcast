using Xunit;
using LocalCast.Core.Discovery;
using LocalCast.Core.Protocol;

namespace LocalCast.Core.Tests;

public class MdnsDiscoveryServiceTests
{
    [Fact]
    public void ManualTvAdditionFiresEventAndAddsToCollection()
    {
        using var service = new MdnsDiscoveryService();
        DiscoveredTv? discovered = null;
        service.TvDiscovered += tv => discovered = tv;

        service.AddManualTv("192.168.1.150", 8765, "TV Sala Reunião");

        Assert.NotNull(discovered);
        Assert.Equal("192.168.1.150", discovered.IpAddress);
        Assert.Equal(8765, discovered.Port);
        Assert.Equal("TV Sala Reunião", discovered.Name);
        Assert.Equal(TvState.Available, discovered.State);
        Assert.True(discovered.IsAvailable);
        Assert.Single(service.DiscoveredTvs);
    }
}
