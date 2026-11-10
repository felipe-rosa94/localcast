using System.Diagnostics;
using LocalCast.Core.Audio;
using LocalCast.Core.Capture;
using LocalCast.Core.Discovery;
using LocalCast.Core.Encoder;
using LocalCast.Core.Protocol;
using LocalCast.Core.Rtc;
using LocalCast.Core.Signaling;
using LocalCast.Core.Video;

namespace LocalCast.Core.Session;

public record SessionMetrics(
    double CaptureFps,
    double CurrentBitrateMbps,
    double RoundTripTimeMs,
    long TotalFramesSent,
    TimeSpan ElapsedDuration
);

public class CastSession : IAsyncDisposable
{
    private readonly DiscoveredTv _tv;
    private readonly SignalingClient _signaling;
    private readonly IScreenCapture? _capture;
    private readonly IHardwareEncoder? _encoder;
    private readonly IVideoPipeline? _pipeline;
    private readonly FfmpegAudioPipeline? _audio;
    private readonly StreamSettings _settings;
    private readonly int _bitrateKbps;

    private WebRtcSender? _rtc;
    private readonly object _iceLock = new();
    private readonly List<(string Candidate, string? Mid, int? Index)> _pendingIce = new();
    private bool _offerSent;
    private long _bytesSent;
    private long _bytesAtLastCalc;

    private readonly Stopwatch _stopwatch = new();
    private long _framesSent;
    private DateTime _lastMetricCalc = DateTime.UtcNow;
    private long _framesAtLastCalc;

    public event Action<SessionMetrics>? MetricsUpdated;
    public event Action<string>? Disconnected;

    public bool IsActive { get; private set; }
    public string TvName => _tv.Name;

    public CastSession(
        DiscoveredTv tv,
        SignalingClient signaling,
        IScreenCapture capture,
        IHardwareEncoder encoder)
    {
        _tv = tv;
        _signaling = signaling;
        _capture = capture;
        _encoder = encoder;

        _settings = StreamSettings.Default;
        _bitrateKbps = 8000;

        _signaling.Disconnected += () => Disconnected?.Invoke("Receptor desconectado");
        _signaling.ErrorReceived += err => Disconnected?.Invoke(err);
    }

    public CastSession(
        DiscoveredTv tv,
        SignalingClient signaling,
        IVideoPipeline pipeline,
        StreamSettings? settings = null,
        int bitrateKbps = 10000,
        FfmpegAudioPipeline? audio = null)
    {
        _tv = tv;
        _signaling = signaling;
        _pipeline = pipeline;
        _audio = audio;
        _settings = settings ?? StreamSettings.Default;
        _bitrateKbps = bitrateKbps;

        _signaling.Disconnected += () => Disconnected?.Invoke("Receptor desconectado");
        _signaling.ErrorReceived += err => Disconnected?.Invoke(err);
    }

    public async Task<(bool Success, string? Error)> StartAsync(string pin, string clientName)
    {
        await _signaling.ConnectAsync(_tv.IpAddress, _tv.Port);

        var (authSuccess, token, authError) = await _signaling.AuthenticateAsync(pin, clientName);
        if (!authSuccess)
        {
            return (false, authError ?? "Falha de pareamento");
        }

        if (_pipeline != null)
        {
            return await StartWebRtcAsync();
        }

        // Initialize Encoder & Capture (simulated path used by tests)
        _encoder!.Initialize(1920, 1080, 60, 8000);
        _capture!.FrameArrived += OnFrameArrived;
        _encoder.EncodedPacketArrived += OnEncodedPacketArrived;

        _capture!.Start(60);
        _stopwatch.Restart();
        IsActive = true;

        return (true, null);
    }

    private async Task<(bool Success, string? Error)> StartWebRtcAsync()
    {
        var pipeline = _pipeline!;
        var rtc = new WebRtcSender();
        _rtc = rtc;

        rtc.IceCandidateGenerated += OnLocalIceCandidate;
        rtc.Connected += OnRtcConnected;
        rtc.Failed += Fail;
        _signaling.AnswerReceived += OnAnswerReceived;
        _signaling.IceCandidateReceived += OnRemoteIceCandidate;
        pipeline.PacketArrived += OnRtpPacket;
        pipeline.Failed += Fail;
        if (_audio != null)
        {
            _audio.PacketArrived += OnAudioPacket;
            _audio.Failed += OnAudioFailed;
        }

        _stopwatch.Restart();
        IsActive = true;

        try
        {
            var offer = await rtc.CreateOfferAsync();
            await _signaling.SendOfferAsync(offer);

            List<(string Candidate, string? Mid, int? Index)> queued;
            lock (_iceLock)
            {
                _offerSent = true;
                queued = new(_pendingIce);
                _pendingIce.Clear();
            }

            foreach (var c in queued)
            {
                await _signaling.SendIceCandidateAsync(c.Candidate, c.Mid, c.Index);
            }
        }
        catch (Exception ex)
        {
            await StopAsync();
            return (false, "Falha ao iniciar a negociação de vídeo: " + ex.Message);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (IsActive && _rtc is { IsConnected: false })
            {
                Fail("A TV não concluiu a conexão de vídeo em 15 s.");
            }
        });

        return (true, null);
    }

    private void OnLocalIceCandidate(string candidate, string? mid, int? index)
    {
        lock (_iceLock)
        {
            if (!_offerSent)
            {
                _pendingIce.Add((candidate, mid, index));
                return;
            }
        }

        _ = _signaling.SendIceCandidateAsync(candidate, mid, index);
    }

    private void OnAnswerReceived(string sdp) => _rtc?.SetAnswer(sdp);

    private void OnRemoteIceCandidate(IceCandidateMessage ice) =>
        _rtc?.AddRemoteCandidate(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex);

    private void OnRtcConnected()
    {
        _ = Task.Run(async () =>
        {
            var (ok, error) = await _pipeline!.StartAsync(_settings, _bitrateKbps);
            if (!ok) Fail(error ?? "Falha ao iniciar a captura de tela.");
        });

        if (_audio == null) return;
        _ = Task.Run(async () =>
        {
            var (ok, error) = await _audio.StartAsync();
            if (!ok) OnAudioFailed(error ?? "Falha ao iniciar o áudio.");
        });
    }

    private void OnAudioPacket(RtpPacket packet)
    {
        if (!IsActive) return;
        _rtc?.SendAudioRtp(packet);
        Interlocked.Add(ref _bytesSent, packet.WireLength);
    }

    private void OnAudioFailed(string message) => Debug.WriteLine("[LocalCast] áudio desativado: " + message);

    private void OnRtpPacket(RtpPacket packet)
    {
        if (!IsActive) return;

        _rtc?.SendRtp(packet);
        Interlocked.Add(ref _bytesSent, packet.WireLength);

        if (!packet.Marker) return;

        Interlocked.Increment(ref _framesSent);

        var now = DateTime.UtcNow;
        var elapsedSec = (now - _lastMetricCalc).TotalSeconds;
        if (elapsedSec < 1.0) return;

        var fps = (_framesSent - _framesAtLastCalc) / elapsedSec;
        var bytes = Interlocked.Read(ref _bytesSent);
        var mbps = (bytes - _bytesAtLastCalc) * 8 / elapsedSec / 1_000_000.0;

        _lastMetricCalc = now;
        _framesAtLastCalc = _framesSent;
        _bytesAtLastCalc = bytes;

        MetricsUpdated?.Invoke(new SessionMetrics(
            CaptureFps: Math.Round(fps, 1),
            CurrentBitrateMbps: Math.Round(mbps, 2),
            RoundTripTimeMs: 2.0,
            TotalFramesSent: _framesSent,
            ElapsedDuration: _stopwatch.Elapsed));
    }

    private void Fail(string message)
    {
        if (!IsActive) return;
        Disconnected?.Invoke(message);
    }

    private void OnFrameArrived(CapturedFrame frame)
    {
        if (!IsActive) return;

        if (frame.RgbaData != null)
        {
            _encoder!.EncodeFrame(frame.RgbaData, frame.TimestampNs);
        }

        // Send cursor update if present
        if (frame.CursorVisible)
        {
            _ = _signaling.SendCursorAsync(
                (double)frame.CursorX / frame.Width,
                (double)frame.CursorY / frame.Height,
                true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
        }
    }

    private void OnEncodedPacketArrived(EncodedPacket packet)
    {
        if (!IsActive) return;

        Interlocked.Increment(ref _framesSent);

        var now = DateTime.UtcNow;
        var elapsedSec = (now - _lastMetricCalc).TotalSeconds;
        if (elapsedSec >= 1.0)
        {
            var framesInSec = _framesSent - _framesAtLastCalc;
            var fps = framesInSec / elapsedSec;
            var bitrateMbps = (fps * packet.Data.Length * 8) / 1_000_000.0;

            _lastMetricCalc = now;
            _framesAtLastCalc = _framesSent;

            MetricsUpdated?.Invoke(new SessionMetrics(
                CaptureFps: Math.Round(fps, 1),
                CurrentBitrateMbps: Math.Round(bitrateMbps, 2),
                RoundTripTimeMs: 2.0, // Local LAN nominal latency
                TotalFramesSent: _framesSent,
                ElapsedDuration: _stopwatch.Elapsed
            ));
        }
    }

    public async Task StopAsync()
    {
        if (!IsActive) return;
        IsActive = false;

        _capture?.Stop();
        if (_capture != null) _capture.FrameArrived -= OnFrameArrived;
        if (_encoder != null) _encoder.EncodedPacketArrived -= OnEncodedPacketArrived;

        if (_pipeline != null)
        {
            _pipeline.PacketArrived -= OnRtpPacket;
            _pipeline.Failed -= Fail;
            _pipeline.Stop();
        }

        if (_audio != null)
        {
            _audio.PacketArrived -= OnAudioPacket;
            _audio.Failed -= OnAudioFailed;
            _audio.Stop();
        }

        if (_rtc != null)
        {
            _signaling.AnswerReceived -= OnAnswerReceived;
            _signaling.IceCandidateReceived -= OnRemoteIceCandidate;
            _rtc.Dispose();
            _rtc = null;
        }

        _stopwatch.Stop();

        await _signaling.DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _capture?.Dispose();
        _encoder?.Dispose();
        _pipeline?.Dispose();
        _audio?.Dispose();
        await _signaling.DisposeAsync();
    }
}
