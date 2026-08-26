using System.ComponentModel;
using MUX.Virtual.App.Models;
using MUX.Virtual.App.Services;

namespace MUX.Virtual.App;

public partial class MainWindow : Window
{
    private const int HotkeyIdReleaseCursor = 0x4D58;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkEscape = 0x1B;
    private const int WmHotkey = 0x0312;

    private readonly SharedStateStore _stateStore = new();
    private readonly DriverPackageService _driverService = new();
    private readonly VirtualDisplayEngine _engine = new();

    private MuxState _state = new();
    private IReadOnlyList<VirtualMonitorPlan> _plan =
        Array.Empty<VirtualMonitorPlan>();

    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        if (!RegisterHotKey(
            helper.Handle,
            HotkeyIdReleaseCursor,
            ModControl | ModAlt,
            VkEscape))
        {
            DriverText.Text =
                "Warning: Ctrl + Alt + Esc could not be registered. Stop the virtual displays from this window to release the cursor.";
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_engine.IsRunning)
        {
            SetStatus(
                "Stop MUX Virtual before reloading layouts.",
                running: true);
            return;
        }

        _state = await _stateStore.LoadAsync();

        LayoutPicker.ItemsSource = null;
        LayoutPicker.DisplayMemberPath = nameof(LayoutProfile.Name);
        LayoutPicker.ItemsSource = _state.Layouts;

        var selected = _state.Layouts.FirstOrDefault(x =>
            x.Id == _state.ActiveLayoutId) ?? _state.Layouts.FirstOrDefault();

        LayoutPicker.SelectedItem = selected;

        DriverText.Text = _driverService.PackageIsBundled
            ? _driverService.TestCertificateIsBundled
                ? "MUX Virtual driver package and development signing certificate are bundled and ready."
                : "MUX Virtual driver package is bundled. This build does not include a development signing certificate."
            : "Driver package is missing. Download the MUX Virtual release ZIP, not the Standard ZIP.";

        RefreshPlan();
    }

    private void RefreshPlan()
    {
        MonitorList.ItemsSource = null;
        _plan = Array.Empty<VirtualMonitorPlan>();

        if (LayoutPicker.SelectedItem is not LayoutProfile layout)
        {
            LayoutMeta.Text =
                "No saved layout found. Create and save a layout in MUX Standard first.";
            return;
        }

        try
        {
            _plan = VirtualDisplayPlanner.Build(_state, layout);

            var display = _state.Displays.FirstOrDefault(x =>
                string.Equals(
                    x.DeviceName,
                    layout.DisplayDeviceName,
                    StringComparison.OrdinalIgnoreCase));

            LayoutMeta.Text =
                display is null
                    ? $"{_plan.Count} monitor(s)"
                    : $"{display.FriendlyName} · {display.WidthPx}×{display.HeightPx} · {_plan.Count} virtual monitor(s)";

            MonitorList.ItemsSource = _plan.Select(x => new MonitorRow(
                x.Name,
                x.ZoneId,
                $"{x.Width} × {x.Height} @ {x.RefreshRate} Hz",
                $"Host {x.HostRect.Left},{x.HostRect.Top}"));
        }
        catch (Exception ex)
        {
            LayoutMeta.Text = ex.Message;
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private void LayoutPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RefreshPlan();
    }

    private async void InstallDriverButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SetBusy(true);
        SetStatus("Installing driver…", running: false);

        try
        {
            var result = await _driverService.InstallAsync();

            if (result.TrustRequired)
            {
                var choice = MessageBox.Show(
                    this,
                    "This development build is test-signed. To install it, MUX must add the included public build certificate to this PC's Local Computer Trusted Root and Trusted Publishers stores.\n\nOnly continue on a machine you control. The certificate contains no private key.\n\nTrust this MUX development certificate and retry the driver installation?",
                    "Trust MUX development driver?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (choice == MessageBoxResult.Yes)
                {
                    SetStatus(
                        "Trusting development certificate and installing driver…",
                        running: false);
                    result = await _driverService.InstallAsync(
                        allowTestCertificateTrust: true);
                }
                else
                {
                    SetStatus("Driver installation cancelled", running: false);
                    DriverText.Text =
                        "The driver was not installed because the development signing certificate was not trusted.";
                    return;
                }
            }

            SetDriverOutput(result.Output);

            SetStatus(
                result.Success
                    ? "Driver installed"
                    : "Driver installation needs attention",
                running: false);
        }
        catch (Exception ex)
        {
            SetStatus("Driver installation failed", running: false);
            DriverText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ActivateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_plan.Count == 0)
        {
            SetStatus("Select a valid layout first.", running: false);
            return;
        }

        SetBusy(true);
        SetStatus("Creating true Windows monitors…", running: false);

        try
        {
            await _engine.StartAsync(_plan);

            SetStatus(
                $"{_engine.ActiveMonitors.Count} virtual monitor(s) active",
                running: true);

            StopButton.IsEnabled = true;
            ActivateButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;
            LayoutPicker.IsEnabled = false;

            DriverText.Text =
                "Move the pointer into a MUX portal to enter that real Windows monitor. Maximize and F11 now use the virtual monitor bounds. Press Ctrl + Alt + Esc to return the cursor to the physical display.";
        }
        catch (Exception ex)
        {
            SetStatus("Activation failed", running: false);
            DriverText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StopButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        StopEngine();
    }

    private void DisplaySettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(
            "ms-settings:display")
        {
            UseShellExecute = true
        });
    }

    private void StopEngine()
    {
        _engine.Stop();
        SetStatus("Stopped", running: false);

        StopButton.IsEnabled = false;
        ActivateButton.IsEnabled = true;
        ReloadButton.IsEnabled = true;
        LayoutPicker.IsEnabled = true;

        DriverText.Text =
            _driverService.PackageIsBundled
                ? "MUX Virtual driver package is bundled and ready."
                : "Driver package is missing.";
    }

    private void SetBusy(bool busy)
    {
        InstallDriverButton.IsEnabled = !busy;
        DisplaySettingsButton.IsEnabled = !busy;
        if (!_engine.IsRunning)
        {
            ActivateButton.IsEnabled = !busy;
            ReloadButton.IsEnabled = !busy;
        }
    }

    private void SetDriverOutput(string output)
    {
        const int maxCharacters = 1800;
        DriverText.Text = output.Length > maxCharacters
            ? output[..maxCharacters] + "…"
            : output;
    }

    private void SetStatus(string text, bool running)
    {
        StatusText.Text = text;
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                running ? "#7BE495" : "#737985"));
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmHotkey &&
            wParam.ToInt32() == HotkeyIdReleaseCursor &&
            _engine.IsRunning)
        {
            _engine.ReleaseCursor();
            Activate();
            Topmost = true;
            Topmost = false;
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        try
        {
            if (_hwndSource is not null)
            {
                UnregisterHotKey(
                    _hwndSource.Handle,
                    HotkeyIdReleaseCursor);
                _hwndSource.RemoveHook(WndProc);
            }
        }
        catch
        {
        }

        _engine.Dispose();
    }

    private sealed record MonitorRow(
        string Name,
        Guid ZoneId,
        string Resolution,
        string HostPosition);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
}
