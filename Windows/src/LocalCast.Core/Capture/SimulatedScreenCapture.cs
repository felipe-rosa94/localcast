namespace LocalCast.Core.Capture;

public class SimulatedScreenCapture : IScreenCapture
{
    public event Action<CapturedFrame>? FrameArrived;

    public int Width { get; private set; } = 1920;
    public int Height { get; private set; } = 1080;
    public int Fps { get; private set; } = 60;
    public bool IsCapturing { get; private set; }
    public bool CaptureCursorInFrame { get; set; } = true;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;

    public void Start(int targetFps = 60)
    {
        if (IsCapturing) return;

        Fps = targetFps;
        IsCapturing = true;
        _cts = new CancellationTokenSource();

        _captureTask = Task.Run(async () =>
        {
            var delayMs = 1000 / targetFps;
            var frameBuffer = new byte[Width * Height * 4];

            while (!_cts.Token.IsCancellationRequested)
            {
                var timestamp = DateTime.UtcNow.Ticks * 100;
                FrameArrived?.Invoke(new CapturedFrame(
                    Width,
                    Height,
                    frameBuffer,
                    IntPtr.Zero,
                    timestamp,
                    CursorX: 100,
                    CursorY: 100,
                    CursorVisible: true
                ));

                await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
            }
        }, _cts.Token);
    }

    public void Stop()
    {
        if (!IsCapturing) return;

        _cts?.Cancel();
        IsCapturing = false;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
