namespace LocalCast.Core.Video;

public interface IVideoPipeline : IDisposable
{
    event Action<RtpPacket>? PacketArrived;
    event Action<string>? Failed;

    string? ActiveEncoder { get; }

    Task<(bool Ok, string? Error)> StartAsync(StreamSettings settings, int bitrateKbps, CancellationToken cancellationToken = default);
    void Stop();
}
