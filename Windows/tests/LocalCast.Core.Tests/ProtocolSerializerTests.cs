using Xunit;
using LocalCast.Core.Protocol;

namespace LocalCast.Core.Tests;

public class ProtocolSerializerTests
{
    [Fact]
    public void SerializesAndDeserializesAuthRequest()
    {
        var original = new AuthRequestMessage("1234", "Developer-PC");
        var json = ProtocolSerializer.Serialize(original);

        Assert.Contains("\"type\":\"auth\"", json);
        Assert.Contains("\"pin\":\"1234\"", json);
        Assert.Contains("\"clientName\":\"Developer-PC\"", json);

        var deserialized = ProtocolSerializer.Deserialize(json) as AuthRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("1234", deserialized.Pin);
        Assert.Equal("Developer-PC", deserialized.ClientName);
    }

    [Fact]
    public void SerializesAndDeserializesSdpOffer()
    {
        var original = new SdpOfferMessage("v=0\r\no=- 123456 2 IN IP4 127.0.0.1");
        var json = ProtocolSerializer.Serialize(original);

        var deserialized = ProtocolSerializer.Deserialize(json) as SdpOfferMessage;
        Assert.NotNull(deserialized);
        Assert.Equal(original.Sdp, deserialized.Sdp);
    }

    [Fact]
    public void SerializesAndDeserializesIceCandidate()
    {
        var original = new IceCandidateMessage("candidate:1 1 UDP 2122260223 192.168.1.50 50000 typ host", "0", 0);
        var json = ProtocolSerializer.Serialize(original);

        var deserialized = ProtocolSerializer.Deserialize(json) as IceCandidateMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("0", deserialized.SdpMid);
        Assert.Equal(0, deserialized.SdpMLineIndex);
    }

    [Fact]
    public void SerializesAndDeserializesCursorUpdate()
    {
        var original = new CursorUpdateMessage(0.45, 0.85, true, 1731154000);
        var json = ProtocolSerializer.Serialize(original);

        var deserialized = ProtocolSerializer.Deserialize(json) as CursorUpdateMessage;
        Assert.NotNull(deserialized);
        Assert.Equal(0.45, deserialized.X);
        Assert.Equal(0.85, deserialized.Y);
        Assert.True(deserialized.Visible);
        Assert.Equal(1731154000, deserialized.TimestampMs);
    }
}
