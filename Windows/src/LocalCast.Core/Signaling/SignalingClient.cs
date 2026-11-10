using System.Net.WebSockets;
using System.Text;
using LocalCast.Core.Protocol;

namespace LocalCast.Core.Signaling;

public class SignalingClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public event Action<string>? AnswerReceived;
    public event Action<IceCandidateMessage>? IceCandidateReceived;
    public event Action<string>? ErrorReceived;
    public event Action? Disconnected;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();

        _ws = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var uri = new Uri($"ws://{ipAddress}:{port}");
        await _ws.ConnectAsync(uri, _cts.Token);

        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task<(bool Success, string? SessionToken, string? Error)> AuthenticateAsync(
        string pin,
        string clientName,
        TimeSpan timeout = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            return (false, null, "Não conectado ao receptor.");
        }

        if (timeout == default) timeout = TimeSpan.FromSeconds(5);

        var tcs = new TaskCompletionSource<(bool, string?, string?)>();

        Action<SignalingMessage>? handler = null;
        handler = msg =>
        {
            if (msg is AuthOkMessage ok)
            {
                tcs.TrySetResult((true, ok.SessionToken, null));
            }
            else if (msg is ErrorMessage err)
            {
                tcs.TrySetResult((false, null, err.Message));
            }
        };

        _messageReceived += handler;

        try
        {
            var req = new AuthRequestMessage(pin, clientName);
            await SendMessageAsync(req);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetResult((false, null, "Tempo limite de autenticação esgotado."))))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _messageReceived -= handler;
        }
    }

    public async Task SendOfferAsync(string sdp)
    {
        await SendMessageAsync(new SdpOfferMessage(sdp));
    }

    public async Task SendIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        await SendMessageAsync(new IceCandidateMessage(candidate, sdpMid, sdpMLineIndex));
    }

    public async Task SendCursorAsync(double x, double y, bool visible, long timestampMs)
    {
        await SendMessageAsync(new CursorUpdateMessage(x, y, visible, timestampMs));
    }

    public async Task SendMessageAsync(SignalingMessage message)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        var json = ProtocolSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private event Action<SignalingMessage>? _messageReceived;

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[16384];
        var ms = new MemoryStream();

        try
        {
            while (!token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                ms.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    var msg = ProtocolSerializer.Deserialize(json);
                    if (msg != null)
                    {
                        _messageReceived?.Invoke(msg);

                        switch (msg)
                        {
                            case SdpAnswerMessage ans:
                                AnswerReceived?.Invoke(ans.Sdp);
                                break;
                            case IceCandidateMessage ice:
                                IceCandidateReceived?.Invoke(ice);
                                break;
                            case ErrorMessage err:
                                ErrorReceived?.Invoke(err.Message);
                                break;
                            case DisconnectMessage:
                                Disconnected?.Invoke();
                                break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel
        }
        catch
        {
            // Connection lost
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
                }
            }
            catch
            {
                // Ignore socket close error
            }
            finally
            {
                _ws.Dispose();
                _ws = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
