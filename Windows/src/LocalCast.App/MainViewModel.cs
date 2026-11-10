using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using LocalCast.Core.Audio;
using LocalCast.Core.Capture;
using LocalCast.Core.Discovery;
using LocalCast.Core.Encoder;
using LocalCast.Core.Protocol;
using LocalCast.Core.Session;
using LocalCast.Core.Signaling;
using LocalCast.Core.Video;

namespace LocalCast.App;

public class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDiscoveryService _discoveryService;
    private readonly Dispatcher _dispatcher;
    private CastSession? _activeSession;

    private bool _isScanning = true;
    private bool _isStreaming;
    private bool _isPairing;
    private bool _isManualConnectOpen;
    private string _pinInput = string.Empty;
    private string _pairingErrorMessage = string.Empty;
    private string _manualIpInput = string.Empty;
    private string _manualPortInput = "8765";
    private string _quickIpInput = string.Empty;

    private string _connectedTvName = string.Empty;
    private string _streamingDuration = "00:00";
    private string _streamingFps = "0 FPS";
    private string _streamingBitrate = "0.0 Mbps";
    private string _streamingLatency = "< 2 ms";

    private DiscoveredTv? _selectedTv;

    public ObservableCollection<DiscoveredTv> DiscoveredTvs { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsScanning
    {
        get => _isScanning;
        set => SetField(ref _isScanning, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetField(ref _isStreaming, value);
    }

    public bool IsPairing
    {
        get => _isPairing;
        set => SetField(ref _isPairing, value);
    }

    public bool IsManualConnectOpen
    {
        get => _isManualConnectOpen;
        set => SetField(ref _isManualConnectOpen, value);
    }

    public string PinInput
    {
        get => _pinInput;
        set => SetField(ref _pinInput, value);
    }

    public string PairingErrorMessage
    {
        get => _pairingErrorMessage;
        set => SetField(ref _pairingErrorMessage, value);
    }

    public string ManualIpInput
    {
        get => _manualIpInput;
        set => SetField(ref _manualIpInput, value);
    }

    public string ManualPortInput
    {
        get => _manualPortInput;
        set => SetField(ref _manualPortInput, value);
    }

    public string QuickIpInput
    {
        get => _quickIpInput;
        set => SetField(ref _quickIpInput, value);
    }

    public string ConnectedTvName
    {
        get => _connectedTvName;
        set => SetField(ref _connectedTvName, value);
    }

    public string StreamingDuration
    {
        get => _streamingDuration;
        set => SetField(ref _streamingDuration, value);
    }

    public string StreamingFps
    {
        get => _streamingFps;
        set => SetField(ref _streamingFps, value);
    }

    public string StreamingBitrate
    {
        get => _streamingBitrate;
        set => SetField(ref _streamingBitrate, value);
    }

    public string StreamingLatency
    {
        get => _streamingLatency;
        set => SetField(ref _streamingLatency, value);
    }

    public DiscoveredTv? SelectedTv
    {
        get => _selectedTv;
        set => SetField(ref _selectedTv, value);
    }

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _discoveryService = new MdnsDiscoveryService();

        _discoveryService.TvDiscovered += OnTvDiscovered;
        _discoveryService.TvLost += OnTvLost;

        _ = InitializeDiscoveryAsync();
    }

    private async Task InitializeDiscoveryAsync()
    {
        try
        {
            await _discoveryService.StartAsync();
        }
        catch
        {
            // Logging
        }
    }

    private void OnTvDiscovered(DiscoveredTv tv)
    {
        _dispatcher.Invoke(() =>
        {
            var existing = DiscoveredTvs.FirstOrDefault(t => t.Id == tv.Id);
            if (existing != null)
            {
                var idx = DiscoveredTvs.IndexOf(existing);
                DiscoveredTvs[idx] = tv;
            }
            else
            {
                DiscoveredTvs.Add(tv);
            }
            IsScanning = false;
        });
    }

    private void OnTvLost(string id)
    {
        _dispatcher.Invoke(() =>
        {
            var existing = DiscoveredTvs.FirstOrDefault(t => t.Id == id);
            if (existing != null)
            {
                DiscoveredTvs.Remove(existing);
            }
        });
    }

    public void SelectTv(DiscoveredTv tv)
    {
        if (tv.State != TvState.Available) return;

        SelectedTv = tv;
        PinInput = string.Empty;
        PairingErrorMessage = string.Empty;
        IsPairing = true;
    }

    public void CancelPairing()
    {
        IsPairing = false;
        SelectedTv = null;
        PinInput = string.Empty;
        PairingErrorMessage = string.Empty;
    }

    private bool _isConnecting;

    public async Task ConfirmPairingAsync()
    {
        if (_isConnecting) return;
        _isConnecting = true;
        try
        {
            await ConfirmPairingCoreAsync();
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private async Task ConfirmPairingCoreAsync()
    {
        if (SelectedTv == null) return;
        if (string.IsNullOrWhiteSpace(PinInput) || PinInput.Length < 4)
        {
            PairingErrorMessage = "Digite o PIN de 4 dígitos exibido na TV.";
            return;
        }

        PairingErrorMessage = "Conectando...";

        if (FfmpegVideoPipeline.FindFfmpeg() == null)
        {
            PairingErrorMessage = "ffmpeg.exe não encontrado. Execute tools\\get-ffmpeg.ps1 e reinicie o LocalCast.";
            return;
        }

        var signaling = new SignalingClient();
        var pipeline = new FfmpegVideoPipeline
        {
            Log = msg => System.Diagnostics.Debug.WriteLine("[LocalCast] " + msg)
        };

        var audio = new FfmpegAudioPipeline
        {
            Log = msg => System.Diagnostics.Debug.WriteLine("[LocalCast] " + msg)
        };

        _activeSession = new CastSession(SelectedTv, signaling, pipeline, audio: audio);
        _activeSession.MetricsUpdated += OnMetricsUpdated;
        _activeSession.Disconnected += OnSessionDisconnected;

        string machineName = Environment.MachineName;
        var (success, error) = await _activeSession.StartAsync(PinInput, machineName);

        if (!success)
        {
            PairingErrorMessage = error ?? "PIN incorreto ou conexão recusada.";
            await _activeSession.DisposeAsync();
            _activeSession = null;
            return;
        }

        ConnectedTvName = SelectedTv.Name;
        IsPairing = false;
        IsStreaming = true;
    }

    public async Task StopStreamingAsync()
    {
        if (_activeSession != null)
        {
            await _activeSession.StopAsync();
            await _activeSession.DisposeAsync();
            _activeSession = null;
        }

        IsStreaming = false;
        ConnectedTvName = string.Empty;
    }

    public void QuickConnect()
    {
        if (string.IsNullOrWhiteSpace(QuickIpInput)) return;

        var cleanIp = QuickIpInput.Trim();
        var id = $"{cleanIp}:8765";
        var tv = new DiscoveredTv(id, $"LocalCast TV ({cleanIp})", cleanIp, 8765, TvState.Available, DateTime.UtcNow);

        var existing = DiscoveredTvs.FirstOrDefault(t => t.Id == id);
        if (existing == null)
        {
            DiscoveredTvs.Insert(0, tv);
        }

        SelectTv(tv);
    }

    public void OpenManualConnect()
    {
        ManualIpInput = string.Empty;
        ManualPortInput = "8765";
        IsManualConnectOpen = true;
    }

    public void CloseManualConnect()
    {
        IsManualConnectOpen = false;
    }

    public void ConfirmManualConnect()
    {
        if (string.IsNullOrWhiteSpace(ManualIpInput)) return;

        int.TryParse(ManualPortInput, out int port);
        if (port <= 0) port = 8765;

        var id = $"{ManualIpInput}:{port}";
        var manualTv = new DiscoveredTv(id, $"TV Manual ({ManualIpInput})", ManualIpInput, port, TvState.Available, DateTime.UtcNow);

        var existing = DiscoveredTvs.FirstOrDefault(t => t.Id == id);
        if (existing == null)
        {
            DiscoveredTvs.Add(manualTv);
        }

        IsManualConnectOpen = false;
        SelectTv(manualTv);
    }

    public async Task RefreshAsync()
    {
        IsScanning = true;
        await _discoveryService.RefreshAsync();
        await Task.Delay(1000);
        IsScanning = false;
    }

    private void OnMetricsUpdated(SessionMetrics metrics)
    {
        _dispatcher.Invoke(() =>
        {
            StreamingDuration = metrics.ElapsedDuration.ToString(@"mm\:ss");
            StreamingFps = $"{metrics.CaptureFps:0} FPS";
            StreamingBitrate = $"{metrics.CurrentBitrateMbps:0.0} Mbps";
            StreamingLatency = $"{metrics.RoundTripTimeMs:0} ms";
        });
    }

    private void OnSessionDisconnected(string reason)
    {
        _dispatcher.BeginInvoke(async () =>
        {
            await StopStreamingAsync();
            if (reason != "Receptor desconectado")
            {
                MessageBox.Show(reason, "LocalCast", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        await StopStreamingAsync();
        await _discoveryService.StopAsync();
    }
}
