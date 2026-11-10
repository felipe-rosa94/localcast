namespace LocalCast.Core.Capture;

public record CapturedFrame(
    int Width,
    int Height,
    byte[]? RgbaData,
    IntPtr NativeTexturePointer,
    long TimestampNs,
    int CursorX = -1,
    int CursorY = -1,
    bool CursorVisible = false
);

public interface IScreenCapture : IDisposable
{
    event Action<CapturedFrame>? FrameArrived;

    int Width { get; }
    int Height { get; }
    int Fps { get; }
    bool IsCapturing { get; }
    bool CaptureCursorInFrame { get; set; }

    void Start(int targetFps = 60);
    void Stop();
}
