using System.Text.Json;
using LocalCast.Core.Video;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace LocalCast.Core.Rtc;

public sealed class WebRtcSender : IDisposable
{
    private const string H264Fmtp = "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";

    private readonly RTCPeerConnection _pc;
    private volatile bool _connected;

    public event Action<string, string?, int?>? IceCandidateGenerated;
    public event Action? Connected;
    public event Action<string>? Failed;

    public bool IsConnected => _connected;

    public WebRtcSender()
    {
        _pc = new RTCPeerConnection(null);

        var format = new VideoFormat(VideoCodecsEnum.H264, FfmpegVideoPipeline.PayloadType, 90000, H264Fmtp);
        var track = new MediaStreamTrack(new List<VideoFormat> { format }, MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);

        _pc.onicecandidate += candidate =>
        {
            if (candidate == null) return;
            try
            {
                using var doc = JsonDocument.Parse(candidate.toJSON());
                var root = doc.RootElement;
                var text = root.GetProperty("candidate").GetString();
                if (string.IsNullOrEmpty(text)) return;
                if (text.Contains(" typ host", StringComparison.Ordinal) is false) return;

                string? mid = root.TryGetProperty("sdpMid", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "0";
                int? index = root.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                IceCandidateGenerated?.Invoke(text, mid, index);
            }
            catch (Exception ex)
            {
                Failed?.Invoke("Falha ao ler candidato ICE: " + ex.Message);
            }
        };

        _pc.onconnectionstatechange += state =>
        {
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    if (!_connected)
                    {
                        _connected = true;
                        Connected?.Invoke();
                    }
                    break;
                case RTCPeerConnectionState.failed:
                    _connected = false;
                    Failed?.Invoke("A conexão WebRTC com a TV falhou (ICE/DTLS). Verifique firewall e rede.");
                    break;
                case RTCPeerConnectionState.disconnected:
                case RTCPeerConnectionState.closed:
                    _connected = false;
                    break;
            }
        };
    }

    public async Task<string> CreateOfferAsync()
    {
        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        return offer.sdp;
    }

    public void SetAnswer(string sdp)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            Failed?.Invoke($"A TV rejeitou a negociação de vídeo ({result}).");
        }
    }

    public void AddRemoteCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        _pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0),
        });
    }

    public void SendRtp(RtpPacket packet)
    {
        if (!_connected) return;
        _pc.SendRtpRaw(SDPMediaTypesEnum.video, packet.Payload, packet.Timestamp, packet.Marker ? 1 : 0, packet.PayloadType);
    }

    public void Dispose()
    {
        _connected = false;
        try { _pc.close(); } catch { }
    }
}
