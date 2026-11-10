using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalCast.Core.Video;

public sealed class FfmpegVideoPipeline : IVideoPipeline
{
    public const int PayloadType = 102;

    private readonly string? _ffmpegHint;
    private Socket? _socket;
    private Thread? _rxThread;
    private Process? _proc;
    private volatile TaskCompletionSource<bool>? _firstPacket;
    private volatile bool _stopping;

    public event Action<RtpPacket>? PacketArrived;
    public event Action<string>? Failed;

    public string? ActiveEncoder { get; private set; }
    public Action<string>? Log { get; set; }

    public FfmpegVideoPipeline(string? ffmpegPath = null)
    {
        _ffmpegHint = ffmpegPath;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillProcess(_proc);
    }

    public static string? FindFfmpeg(string? hint = null)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string?>
        {
            hint,
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
        }

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // invalid PATH entry
            }
        }

        return null;
    }

    public async Task<(bool Ok, string? Error)> StartAsync(StreamSettings settings, int bitrateKbps, CancellationToken cancellationToken = default)
    {
        Stop();

        var ffmpeg = FindFfmpeg(_ffmpegHint);
        if (ffmpeg == null)
        {
            return (false, "ffmpeg.exe não encontrado. Execute Windows\\tools\\get-ffmpeg.ps1 ou coloque o ffmpeg.exe ao lado do LocalCast.");
        }

        var encodersText = await RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, cancellationToken);
        var filtersText = await RunAsync(ffmpeg, new[] { "-hide_banner", "-filters" }, cancellationToken);
        bool hasDdagrab = filtersText.Contains(" ddagrab ", StringComparison.Ordinal);

        var candidates = BuildCandidates(encodersText);
        if (candidates.Count == 0)
        {
            return (false, "Este ffmpeg não possui encoder H.264 (h264_nvenc, h264_qsv, h264_amf ou libx264).");
        }

        _stopping = false;
        OpenSocket(out int port);

        var errors = new StringBuilder();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ok, tail) = await TryLaunchAsync(ffmpeg, candidate, hasDdagrab, settings, bitrateKbps, port, cancellationToken);
            if (ok)
            {
                ActiveEncoder = candidate.Label;
                Log?.Invoke($"ffmpeg ativo: {candidate.Label} ({(hasDdagrab ? "ddagrab" : "gdigrab")})");
                return (true, null);
            }

            Log?.Invoke($"encoder {candidate.Label} falhou: {tail}");
            errors.AppendLine($"{candidate.Label}: {tail}");
        }

        Stop();
        return (false, "Nenhum encoder H.264 funcionou.\n" + errors);
    }

    public void Stop()
    {
        _stopping = true;
        var proc = _proc;
        _proc = null;
        KillProcess(proc);

        var socket = _socket;
        _socket = null;
        try { socket?.Close(); } catch { }

        var thread = _rxThread;
        _rxThread = null;
        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }

        ActiveEncoder = null;
    }

    public void Dispose() => Stop();

    private sealed record EncoderCandidate(string Label, string Name, string[] Args);

    private static List<EncoderCandidate> BuildCandidates(string encodersText)
    {
        var list = new List<EncoderCandidate>();

        bool Has(string name) => encodersText.Contains(" " + name + " ", StringComparison.Ordinal);

        if (Has("h264_nvenc"))
        {
            list.Add(new("NVENC (baseline)", "h264_nvenc", new[] { "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1", "-bf", "0", "-profile:v", "baseline" }));
            list.Add(new("NVENC", "h264_nvenc", new[] { "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1", "-bf", "0" }));
        }

        if (Has("h264_qsv"))
        {
            list.Add(new("Intel QSV (baseline)", "h264_qsv", new[] { "-preset", "veryfast", "-async_depth", "1", "-bf", "0", "-profile:v", "baseline" }));
            list.Add(new("Intel QSV", "h264_qsv", new[] { "-preset", "veryfast", "-async_depth", "1", "-bf", "0" }));
        }

        if (Has("h264_amf"))
        {
            list.Add(new("AMD AMF (baseline)", "h264_amf", new[] { "-usage", "ultralowlatency", "-quality", "balanced", "-rc", "cbr", "-profile:v", "constrained_baseline" }));
            list.Add(new("AMD AMF", "h264_amf", new[] { "-usage", "ultralowlatency", "-quality", "balanced", "-rc", "cbr" }));
        }

        if (Has("libx264"))
        {
            list.Add(new("x264 (CPU)", "libx264", new[] { "-preset", "superfast", "-tune", "zerolatency", "-profile:v", "baseline", "-x264-params", "repeat-headers=1" }));
        }

        return list;
    }

    private static List<string> BuildArgs(EncoderCandidate enc, bool useDdagrab, StreamSettings s, int bitrateKbps, int port)
    {
        var inv = CultureInfo.InvariantCulture;
        int fps = Math.Clamp(s.Fps, 5, 120);
        int height = Math.Max(240, s.Height);
        int bufKbps = Math.Max(500, bitrateKbps * 3 / fps);
        string scale = $"scale=-2:min(ih\\,{height.ToString(inv)}):flags=fast_bilinear,format=nv12";

        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };

        if (useDdagrab)
        {
            a.AddRange(new[] { "-f", "lavfi", "-i", $"ddagrab=output_idx=0:framerate={fps.ToString(inv)}:draw_mouse=1" });
            a.AddRange(new[] { "-vf", "hwdownload,format=bgra," + scale });
        }
        else
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(inv), "-draw_mouse", "1", "-i", "desktop" });
            a.AddRange(new[] { "-vf", scale });
        }

        a.AddRange(new[] { "-c:v", enc.Name });
        a.AddRange(enc.Args);
        a.AddRange(new[]
        {
            "-b:v", $"{bitrateKbps.ToString(inv)}k",
            "-maxrate", $"{bitrateKbps.ToString(inv)}k",
            "-bufsize", $"{bufKbps.ToString(inv)}k",
            "-g", fps.ToString(inv),
            "-bsf:v", "dump_extra=freq=keyframe",
            "-flush_packets", "1",
            "-f", "rtp",
            "-payload_type", PayloadType.ToString(inv),
            "-pkt_size", "1200",
            $"rtp://127.0.0.1:{port.ToString(inv)}",
        });

        return a;
    }

    private void OpenSocket(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _socket = socket;

        _rxThread = new Thread(() => ReceiveLoop(socket))
        {
            IsBackground = true,
            Name = "LocalCast-RTP-RX",
            Priority = ThreadPriority.AboveNormal,
        };
        _rxThread.Start();
    }

    private void ReceiveLoop(Socket socket)
    {
        var buffer = new byte[2048];
        while (!_stopping)
        {
            int n;
            try
            {
                n = socket.Receive(buffer);
            }
            catch
            {
                break;
            }

            if (!RtpPacketParser.TryParse(buffer.AsSpan(0, n), out var packet)) continue;

            _firstPacket?.TrySetResult(true);

            try
            {
                PacketArrived?.Invoke(packet);
            }
            catch (Exception ex)
            {
                Log?.Invoke("erro ao encaminhar pacote RTP: " + ex.Message);
            }
        }
    }

    private async Task<(bool Ok, string Tail)> TryLaunchAsync(
        string ffmpeg, EncoderCandidate candidate, bool useDdagrab,
        StreamSettings settings, int bitrateKbps, int port, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in BuildArgs(candidate, useDdagrab, settings, bitrateKbps, port))
        {
            psi.ArgumentList.Add(arg);
        }

        var firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstPacket = firstPacket;

        var errLines = new Queue<string>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errLines)
            {
                errLines.Enqueue(e.Data);
                while (errLines.Count > 8) errLines.Dequeue();
            }
        };
        proc.OutputDataReceived += (_, _) => { };
        proc.Exited += (_, _) =>
        {
            firstPacket.TrySetResult(false);
            if (!_stopping && ReferenceEquals(_proc, proc))
            {
                Failed?.Invoke("O ffmpeg encerrou durante a transmissão. " + Tail());
            }
        };

        string Tail()
        {
            lock (errLines) return string.Join(" | ", errLines);
        }

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var finished = await Task.WhenAny(firstPacket.Task, Task.Delay(6000, ct));
        if (finished == firstPacket.Task && firstPacket.Task.Result)
        {
            _proc = proc;
            return (true, string.Empty);
        }

        var tail = Tail();
        KillProcess(proc);
        return (false, string.IsNullOrEmpty(tail) ? "sem pacotes de vídeo em 6 s" : tail);
    }

    internal static void KillProcess(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return await stdout + await stderr;
    }
}
