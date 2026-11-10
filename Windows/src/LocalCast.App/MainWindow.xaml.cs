using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LocalCast.Core.Discovery;

namespace LocalCast.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // título claro em versões antigas do Windows
        }
    }

    private void OnPairingVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PinTextBox.Focus();
                PinTextBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnPinPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private async void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.ConfirmPairingAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CancelPairing();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshAsync();
    }

    private void OnManualConnectClicked(object sender, RoutedEventArgs e)
    {
        _vm.OpenManualConnect();
    }

    private void OnQuickConnectClicked(object sender, RoutedEventArgs e)
    {
        _vm.QuickConnect();
    }

    private void OnQuickIpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.QuickConnect();
        }
    }

    private void OnTvSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DiscoveredTv tv)
        {
            _vm.SelectTv(tv);
        }
    }

    private async void OnStopStreamingClicked(object sender, RoutedEventArgs e)
    {
        await _vm.StopStreamingAsync();
    }

    private void OnCancelPairingClicked(object sender, RoutedEventArgs e)
    {
        _vm.CancelPairing();
    }

    private async void OnConfirmPairingClicked(object sender, RoutedEventArgs e)
    {
        await _vm.ConfirmPairingAsync();
    }

    private void OnCancelManualConnectClicked(object sender, RoutedEventArgs e)
    {
        _vm.CloseManualConnect();
    }

    private void OnConfirmManualConnectClicked(object sender, RoutedEventArgs e)
    {
        _vm.ConfirmManualConnect();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _vm.DisposeAsync();
    }
}
