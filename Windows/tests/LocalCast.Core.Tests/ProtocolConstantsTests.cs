using Xunit;
using LocalCast.Core;
using LocalCast.Core.Protocol;

namespace LocalCast.Core.Tests;

public class ProtocolConstantsTests
{
    [Fact]
    public void PinHasFourDigits() => Assert.Equal(4, ProtocolConstants.PinLength);

    [Fact]
    public void DefaultSettingsFavorFluidity()
    {
        var s = StreamSettings.Default;
        Assert.Equal(1080, s.Height);
        Assert.Equal(30, s.Fps);
        Assert.True(s.Adaptive);
    }
}
