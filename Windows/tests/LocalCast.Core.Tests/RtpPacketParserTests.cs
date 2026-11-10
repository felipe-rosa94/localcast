using LocalCast.Core.Video;
using Xunit;

namespace LocalCast.Core.Tests;

public class RtpPacketParserTests
{
    [Fact]
    public void ParsesHeaderMarkerAndPayload()
    {
        byte[] data =
        {
            0x80, 0xE6, 0x00, 0x01,
            0x00, 0x00, 0x03, 0xE8,
            0xDE, 0xAD, 0xBE, 0xEF,
            0x65, 0x88, 0x84,
        };

        Assert.True(RtpPacketParser.TryParse(data, out var p));
        Assert.True(p.Marker);
        Assert.Equal(102, p.PayloadType);
        Assert.Equal(1000u, p.Timestamp);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84 }, p.Payload);
        Assert.Equal(data.Length, p.WireLength);
    }

    [Fact]
    public void RejectsShortOrWrongVersionPackets()
    {
        Assert.False(RtpPacketParser.TryParse(new byte[5], out _));
        Assert.False(RtpPacketParser.TryParse(new byte[] { 0x40, 0x66, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 9 }, out _));
    }
}
