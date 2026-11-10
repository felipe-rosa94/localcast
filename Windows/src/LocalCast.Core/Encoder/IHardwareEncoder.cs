namespace LocalCast.Core.Encoder;

public record EncodedPacket(
    byte[] Data,
    bool IsKeyFrame,
    long TimestampNs
);

public interface IHardwareEncoder : IDisposable
{
    event Action<EncodedPacket>? EncodedPacketArrived;

    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    int Fps { get; }
    int BitrateKbps { get; }

    void Initialize(int width, int height, int fps = 60, int bitrateKbps = 8000);
    void RequestKeyFrame();
    void EncodeFrame(byte[] rgbaData, long timestampNs);
    void SetBitrate(int bitrateKbps);
}
