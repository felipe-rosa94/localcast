using System.Runtime.InteropServices;

namespace LocalCast.Core.Encoder;

public class MediaFoundationEncoder : IHardwareEncoder
{
    public event Action<EncodedPacket>? EncodedPacketArrived;

    public bool IsInitialized { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public int BitrateKbps { get; private set; }
    public bool IsHardwareAccelerated { get; private set; } = true;

    private bool _keyFrameRequested = true;

    public void Initialize(int width, int height, int fps = 60, int bitrateKbps = 8000)
    {
        Width = width;
        Height = height;
        Fps = fps;
        BitrateKbps = bitrateKbps;
        IsInitialized = true;
        _keyFrameRequested = true;
    }

    public void RequestKeyFrame()
    {
        _keyFrameRequested = true;
    }

    public void SetBitrate(int bitrateKbps)
    {
        BitrateKbps = bitrateKbps;
    }

    public void EncodeFrame(byte[] rgbaData, long timestampNs)
    {
        if (!IsInitialized) return;

        // In a complete hardware pipeline, D3D11 textures are passed directly to the MFT IMFTransform.
        // For testing and pipeline validation, we simulate the output NAL packet with low-latency headers.
        bool isKey = _keyFrameRequested;
        _keyFrameRequested = false;

        // Simulate minimal Annex-B NAL unit (0x00, 0x00, 0x00, 0x01, type)
        byte[] nalu = isKey ? new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1f, 0, 0, 0, 1, 0x65, 0x88 }
                            : new byte[] { 0, 0, 0, 1, 0x41, 0x9a };

        EncodedPacketArrived?.Invoke(new EncodedPacket(nalu, isKey, timestampNs));
    }

    public void Dispose()
    {
        IsInitialized = false;
    }
}
