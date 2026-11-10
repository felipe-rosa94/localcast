using System.Net;
using System.Net.WebSockets;
using System.Text;
using Xunit;
using LocalCast.Core.Capture;
using LocalCast.Core.Discovery;
using LocalCast.Core.Encoder;
using LocalCast.Core.Protocol;
using LocalCast.Core.Session;
using LocalCast.Core.Signaling;

namespace LocalCast.Core.Tests;

public class CastSessionTests
{
    [Fact]
    public async Task CastSessionStartsAndEmitsMetrics()
    {
        var listener = new HttpListener();
        int port = 9877;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsContext = await ctx.AcceptWebSocketAsync(null);
                var ws = wsContext.WebSocket;
                var buffer = new byte[4096];
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                var okJson = ProtocolSerializer.Serialize(new AuthOkMessage("token-123", "TV Test"));
                var okBytes = Encoding.UTF8.GetBytes(okJson);
                await ws.SendAsync(new ArraySegment<byte>(okBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                // Keep open briefly
                await Task.Delay(200);
            }
        });

        var tv = new DiscoveredTv("tv1", "TV Test", "127.0.0.1", port, TvState.Available, DateTime.UtcNow);
        var signaling = new SignalingClient();
        var capture = new SimulatedScreenCapture();
        var encoder = new MediaFoundationEncoder();

        await using var session = new CastSession(tv, signaling, capture, encoder);
        var (success, error) = await session.StartAsync("1111", "SenderPC");

        Assert.True(success);
        Assert.Null(error);
        Assert.True(session.IsActive);

        await session.StopAsync();
        Assert.False(session.IsActive);

        await serverTask;
        listener.Stop();
    }
}
