using System.Net;
using System.Net.WebSockets;
using System.Text;
using Xunit;
using LocalCast.Core.Protocol;
using LocalCast.Core.Signaling;

namespace LocalCast.Core.Tests;

public class SignalingClientTests
{
    [Fact]
    public async Task AuthenticateAsyncReturnsSuccessWhenAuthOkReceived()
    {
        var listener = new HttpListener();
        int port = 9876;
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
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                var reqJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var req = ProtocolSerializer.Deserialize(reqJson) as AuthRequestMessage;

                if (req?.Pin == "4321")
                {
                    var okJson = ProtocolSerializer.Serialize(new AuthOkMessage("test-token", "Mock TV"));
                    var okBytes = Encoding.UTF8.GetBytes(okJson);
                    await ws.SendAsync(new ArraySegment<byte>(okBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        });

        await using var client = new SignalingClient();
        await client.ConnectAsync("127.0.0.1", port);
        var (success, token, error) = await client.AuthenticateAsync("4321", "TestPC");

        Assert.True(success);
        Assert.Equal("test-token", token);
        Assert.Null(error);

        await serverTask;
        listener.Stop();
    }
}
