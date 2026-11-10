using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LocalCast.Core.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCast.Core.Audio;

public sealed class FfmpegAudioPipeline : IDisposable
{
    public const int PayloadType = 111;

    private readonly string? _ffmpegHint;
    private readonly object _writeLock = new();
    private Socket? _socket;
    private Thread? _rxThread;
    private Process? _proc;
    private Stream? _stdin;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _silence;
    private volatile TaskCompletionSource<bool>? _firstPacket;
    private volatile bool _stopping;

    public event Action<RtpPacket>? PacketArrived;
    public event Action<string>? Failed;

    public Action<string>? Log { get; set; }

    public FfmpegAudioPipeline(string? ffmpegPath = null)
    {
        _ffmpegHint = ffmpegPath;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FfmpegVideoPipeline.KillProcess(_proc);
    }

    public async Task<(bool Ok, string? Error)> StartAsync(CancellationToken cancellationToken = default)
    {
        Stop();

        var ffmpeg = FfmpegVideoPipeline.FindFfmpeg(_ffmpegHint);
        if (ffmpeg == null) return (false, "ffmpeg.exe não encontrado.");

        _stopping = false;
        var errLines = new Queue<string>();
        string Tail()
        {
            lock (errLines) return string.Join(" | ", errLines);
        }

        try
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var capture = new WasapiLoopbackCapture(device);
            var fmt = capture.WaveFormat;
            var sampleFormat = fmt.BitsPerSample switch
            {
                32 when fmt.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible => "f32le",
                32 => "s32le",
                24 => "s24le",
                _ => "s16le",
            };

            OpenSocket(out int port);

            var inv = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-fflags", "nobuffer", "-probesize", "32", "-analyzeduration", "0",
                "-f", sampleFormat, "-ar", fmt.SampleRate.ToString(inv), "-ac", fmt.Channels.ToString(inv), "-i", "pipe:0",
                "-ac", "2", "-ar", "48000",
                "-c:a", "libopus", "-application", "lowdelay", "-frame_duration", "10", "-b:a", "160k", "-vbr", "constrained",
                "-flush_packets", "1",
                "-f", "rtp", "-payload_type", PayloadType.ToString(inv),
                $"rtp://127.0.0.1:{port.ToString(inv)}",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            var firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstPacket = firstPacket;

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
                if (!_stopping && ReferenceEquals(_proc, proc)) Failed?.Invoke("O ffmpeg de áudio encerrou. " + Tail());
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            _proc = proc;
            _stdin = proc.StandardInput.BaseStream;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, e) =>
            {
                if (!_stopping && e.Exception != null) Failed?.Invoke("Captura de áudio parou: " + e.Exception.Message);
            };
            _capture = capture;

            // Loopback só entrega amostras enquanto algo toca; um stream de silêncio mantém o fluxo contínuo.
            var silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
            silence.Init(new SilenceProvider(new WaveFormat(fmt.SampleRate, 16, 2)));
            silence.Play();
            _silence = silence;

            capture.StartRecording();

            var finished = await Task.WhenAny(firstPacket.Task, Task.Delay(4000, cancellationToken));
            if (finished == firstPacket.Task && firstPacket.Task.Result)
            {
                Log?.Invoke($"áudio ativo: {fmt.SampleRate} Hz, {fmt.Channels} canais, {sampleFormat} -> Opus");
                return (true, null);
            }
        }
        catch (Exception ex)
        {
            Stop();
            return (false, "Falha ao iniciar o áudio: " + ex.Message);
        }

        var tail = Tail();
        Stop();
        return (false, "Falha ao iniciar o áudio: " + (string.IsNullOrEmpty(tail) ? "sem pacotes em 4 s" : tail));
    }

    public void Stop()
    {
        _stopping = true;

        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        var silence = _silence;
        _silence = null;
        try { silence?.Stop(); } catch { }
        silence?.Dispose();

        lock (_writeLock)
        {
            try { _stdin?.Close(); } catch { }
            _stdin = null;
        }

        var proc = _proc;
        _proc = null;
        FfmpegVideoPipeline.KillProcess(proc);

        var socket = _socket;
        _socket = null;
        try { socket?.Close(); } catch { }

        var thread = _rxThread;
        _rxThread = null;
        if (thread != null && thread != Thread.CurrentThread) thread.Join(500);
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        lock (_writeLock)
        {
            try
            {
                _stdin?.Write(e.Buffer, 0, e.BytesRecorded);
                _stdin?.Flush();
            }
            catch (Exception ex)
            {
                if (!_stopping) Log?.Invoke("erro ao enviar áudio ao ffmpeg: " + ex.Message);
            }
        }
    }

    private void OpenSocket(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _socket = socket;

        _rxThread = new Thread(() => ReceiveLoop(socket))
        {
            IsBackground = true,
            Name = "LocalCast-Audio-RX",
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
                Log?.Invoke("erro ao encaminhar pacote de áudio: " + ex.Message);
            }
        }
    }
}
