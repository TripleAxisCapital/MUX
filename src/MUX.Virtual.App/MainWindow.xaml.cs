using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using MUX.App.Services;
using MUX.App.Windows;
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

    private readonly StateStore _store = new();
    private readonly DisplayDiscoveryService _displayDiscovery = new();
    private readonly VirtualStartupService _startupService = new();
    private readonly DriverPackageService _driverService = new();
    private readonly VirtualDisplayEngine _engine = new();

    private HotkeyService? _hotkeys;
    private HwndSource? _hwndSource;
    private MuxState _state = new();
    private DisplayProfile? _display;
    private LayoutProfile? _layout;
    private IReadOnlyList<VirtualMonitorPlan> _plan = Array.Empty<VirtualMonitorPlan>();
    private bool _loading;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        WorkspaceCanvas.SelectedZoneChanged += WorkspaceCanvas_SelectedZoneChanged;
        WorkspaceCanvas.LayoutChanged += async (_, _) =>
        {
            StopEngineForEdit();
            RefreshWorkspace();
            await SaveStateAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _state = await _store.LoadAsync();
        _state.Shortcuts ??= new ShortcutSettings();
        MergeDetectedDisplays();
        SelectInitialDisplay();
        RefreshAll();
        _loading = false;

        if (_hotkeys is not null)
        {
            if (!_hotkeys.Reload(_state.Shortcuts, out var failedIds))
            {
                StatusText.Text = "Some shortcuts are already in use: " + string.Join(", ", failedIds.Select(ShortcutActionName));
            }
        }

        RefreshShortcutSummary();
        RefreshDriverVisual();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterHotKey(helper.Handle, HotkeyIdReleaseCursor, ModControl | ModAlt, VkEscape);

        _hotkeys = new HotkeyService(this);
        _hotkeys.Register(1, System.Windows.Input.Key.M, () => NativeWindowActions.ToggleForegroundMaximize());
        _hotkeys.Register(2, System.Windows.Input.Key.Left, () => NativeWindowActions.MoveForegroundToAdjacent(_engine.ActiveMonitors, -1));
        _hotkeys.Register(3, System.Windows.Input.Key.Right, () => NativeWindowActions.MoveForegroundToAdjacent(_engine.ActiveMonitors, 1));
        _hotkeys.Register(4, System.Windows.Input.Key.E, () => Dispatcher.Invoke(EditOnDisplay));
        _hotkeys.Register(5, System.Windows.Input.Key.F, () => NativeWindowActions.ToggleForegroundFullscreen());
    }

    private void MergeDetectedDisplays()
    {
        // Virtual targets are output devices, not physical canvases. Never persist them as
        // physical displays when the app is reopened while an old adapter is still present.
        _state.Displays.RemoveAll(d => IsMuxVirtualDisplay(d.FriendlyName));

        var detected = _displayDiscovery.GetDisplays()
            .Where(d => !IsMuxVirtualDisplay(d.FriendlyName))
            .ToList();

        foreach (var current in detected)
        {
            var saved = _state.Displays.FirstOrDefault(d =>
                d.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (saved is null)
            {
                _state.Displays.Add(current);
                continue;
            }

            saved.FriendlyName = current.FriendlyName;
            saved.LeftPx = current.LeftPx;
            saved.TopPx = current.TopPx;
            saved.WidthPx = current.WidthPx;
            saved.HeightPx = current.HeightPx;
            saved.IsPrimary = current.IsPrimary;
        }

        if (_state.Displays.Count == 0)
        {
            _state.Displays.Add(new DisplayProfile
            {
                DeviceName = "DISPLAY",
                FriendlyName = "Display",
                WidthPx = 1920,
                HeightPx = 1080,
                IsPrimary = true
            });
        }
    }

    private static bool IsMuxVirtualDisplay(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Contains("MUX Virtual", StringComparison.OrdinalIgnoreCase);

    private void SelectInitialDisplay()
    {
        _display = _state.Displays.FirstOrDefault(d => d.DeviceName == _state.ActiveDisplayDeviceName)
                   ?? _state.Displays.FirstOrDefault(d => d.IsPrimary)
                   ?? _state.Displays.First();
        _state.ActiveDisplayDeviceName = _display.DeviceName;
        EnsureLayoutForDisplay();
    }

    private void EnsureLayoutForDisplay()
    {
        if (_display is null)
        {
            return;
        }

        var layouts = _state.Layouts.Where(l => l.DisplayDeviceName == _display.DeviceName).ToList();
        if (layouts.Count == 0)
        {
            var layout = new LayoutProfile
            {
                Name = "Workspace",
                DisplayDeviceName = _display.DeviceName
            };
            _state.Layouts.Add(layout);
            layouts.Add(layout);
        }

        _layout = layouts.FirstOrDefault(l => l.Id == _state.ActiveLayoutId) ?? layouts[0];
        _state.ActiveLayoutId = _layout.Id;
    }

    private void RefreshAll()
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        var selectedDisplayName = _display.DeviceName;
        DisplayCombo.ItemsSource = null;
        DisplayCombo.ItemsSource = _state.Displays;
        DisplayCombo.SelectedItem = _state.Displays.FirstOrDefault(d => d.DeviceName == selectedDisplayName);

        var layouts = _state.Layouts.Where(l => l.DisplayDeviceName == _display.DeviceName).ToList();
        LayoutList.ItemsSource = null;
        LayoutList.ItemsSource = layouts;
        LayoutList.SelectedItem = layouts.FirstOrDefault(l => l.Id == _layout.Id);

        DisplaySizeBox.Text = _display.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture);
        var ppi = DisplayGeometry.PixelsPerInch(_display);
        var physical = DisplayGeometry.DisplayPhysicalSize(_display);
        DisplayMetricsText.Text = $"{_display.WidthPx} × {_display.HeightPx} · {ppi:0.#} px/in · {physical.Width:0.#} × {physical.Height:0.#} in usable";

        StartupCheck.IsChecked = _state.LaunchAtStartup || SafeStartupEnabled();
        RefreshWorkspace();
        RefreshZoneInspector(null);
        RefreshEngineVisual();
    }

    private void RefreshWorkspace()
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        LayoutTitle.Text = _layout.Name;
        LayoutSubtitle.Text = _layout.Zones.Count == 1 ? "1 true virtual monitor" : $"{_layout.Zones.Count} true virtual monitors";
        EmptyState.Visibility = _layout.Zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceCanvas.SetModel(_display, _layout);
        RefreshPlan();
    }

    private void RefreshPlan()
    {
        _plan = Array.Empty<VirtualMonitorPlan>();
        if (_layout is null)
        {
            return;
        }

        try
        {
            _plan = VirtualDisplayPlanner.Build(_state, _layout);
            StatusText.Text = _plan.Count == 0
                ? "Add a monitor to activate MUX Virtual."
                : $"Ready · {_plan.Count} Windows monitor{(_plan.Count == 1 ? string.Empty : "s")} planned";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void RefreshEngineVisual()
    {
        var running = _engine.IsRunning;
        EngineSubtitle.Text = running ? $"Active · {_engine.ActiveMonitors.Count} monitors" : "Stopped";
        EngineDot.Background = new SolidColorBrush(running ? Color.FromRgb(140, 255, 176) : Color.FromRgb(98, 98, 106));
        ActivateButton.Content = running ? "Stop virtual displays" : "Activate virtual displays";
        EngineToggleButton.IsEnabled = !_busy;
        ActivateButton.IsEnabled = !_busy && (running || _plan.Count > 0);
    }

    private void RefreshDriverVisual()
    {
        var status = _driverService.GetRuntimeStatus();
        DriverSidebarText.Text = status.Summary;
        DevelopmentModeButton.Visibility = status.DevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;
        DevelopmentModeButton.Content = status.TestSigningEnabled
            ? "Development mode active"
            : "Enable development mode";
        DevelopmentModeButton.IsEnabled = status.DevelopmentBuild && !status.TestSigningEnabled && !_busy;
    }

    private async void EngineToggle_Click(object sender, RoutedEventArgs e) => await ToggleEngineAsync();
    private async void Activate_Click(object sender, RoutedEventArgs e) => await ToggleEngineAsync();

    private async Task ToggleEngineAsync()
    {
        if (_engine.IsRunning)
        {
            StopEngine();
            return;
        }

        if (_plan.Count == 0)
        {
            StatusText.Text = "Create at least one valid monitor first.";
            return;
        }

        var runtime = _driverService.GetRuntimeStatus();
        if (!runtime.PackagePresent)
        {
            StatusText.Text = runtime.Summary;
            return;
        }

        if (runtime.DevelopmentBuild && !runtime.TestSigningEnabled)
        {
            var answer = MessageBox.Show(this,
                "This rolling MUX Virtual build uses a test-signed Windows display driver. The driver is staged, but Windows will not load it until the PC boots with Windows Test Mode enabled.\n\nEnable Test Mode now? A restart is required. MUX will not disable Secure Boot automatically.",
                "MUX Virtual development driver",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                await EnableDevelopmentModeAsync();
            }
            else
            {
                StatusText.Text = "Activation requires Test Mode for this development build, or a Microsoft production-signed driver.";
            }
            return;
        }

        SetBusy(true);
        StatusText.Text = "Creating true Windows monitors…";

        try
        {
            await _engine.StartAsync(_plan);
            StatusText.Text = $"Active · {_engine.ActiveMonitors.Count} true Windows monitor{(_engine.ActiveMonitors.Count == 1 ? string.Empty : "s")}";
        }
        catch (VirtualActivationException ex)
        {
            var diagnostic = await _driverService.GetDeviceDiagnosticsAsync();
            StatusText.Text = ex.Message;
            DriverSidebarText.Text = string.IsNullOrWhiteSpace(diagnostic)
                ? ex.Message
                : ex.Message + "\n\nWindows device diagnostics:\n" + diagnostic;
            App.LogException("Virtual activation: " + ex.Stage, ex);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Activation failed: " + ex.Message;
            DriverSidebarText.Text = ex.Message;
            App.LogException("Virtual activation", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshEngineVisual();
            RefreshDriverVisual();
        }
    }

    private void StopEngine()
    {
        _engine.Stop();
        StatusText.Text = "Virtual displays stopped.";
        RefreshEngineVisual();
    }

    private void StopEngineForEdit()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            StatusText.Text = "Virtual displays stopped while the layout was edited. Activate again when ready.";
            RefreshEngineVisual();
        }
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Installing MUX Virtual display driver…";

        try
        {
            var result = await _driverService.InstallAsync();
            if (result.TrustRequired)
            {
                var answer = MessageBox.Show(this,
                    result.Output + "\n\nTrust this development certificate and retry?",
                    "Trust MUX development driver",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (answer == MessageBoxResult.Yes)
                {
                    result = await _driverService.InstallAsync(allowTestCertificateTrust: true);
                }
            }

            DriverSidebarText.Text = TrimStatus(result.Output, 900);
            StatusText.Text = result.Success ? "Driver package installed." : "Driver installation needs attention.";

            if (result.Success)
            {
                var runtime = _driverService.GetRuntimeStatus();
                if (runtime.DevelopmentBuild && !runtime.TestSigningEnabled)
                {
                    StatusText.Text = "Driver installed · enable development mode and restart before activation.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Driver installation failed.";
            DriverSidebarText.Text = ex.Message;
            App.LogException("Driver install", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshDriverVisual();
            RefreshEngineVisual();
        }
    }

    private async void DevelopmentMode_Click(object sender, RoutedEventArgs e) => await EnableDevelopmentModeAsync();

    private async Task EnableDevelopmentModeAsync()
    {
        var answer = MessageBox.Show(this,
            "Windows Test Mode permits development/test-signed drivers to load. This weakens driver-signing enforcement for this Windows boot configuration and requires a restart.\n\nMUX will only enable the official TESTSIGNING boot option. It will not disable Secure Boot or integrity checks. Continue?",
            "Enable Windows Test Mode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _driverService.EnableDevelopmentModeAsync();
            DriverSidebarText.Text = TrimStatus(result.Output, 900);
            StatusText.Text = result.Success
                ? "Development mode configured · restart Windows, then activate MUX Virtual."
                : "Windows could not enable development mode.";

            if (result.Success)
            {
                MessageBox.Show(this,
                    "Windows Test Mode was enabled. Restart Windows before activating the virtual displays.",
                    "Restart required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            DriverSidebarText.Text = ex.Message;
            StatusText.Text = "Could not configure development mode.";
        }
        finally
        {
            SetBusy(false);
            RefreshDriverVisual();
            RefreshEngineVisual();
        }
    }

    private async void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedItem is not DisplayProfile display || _display?.DeviceName == display.DeviceName)
        {
            return;
        }

        StopEngineForEdit();
        _display = display;
        _state.ActiveDisplayDeviceName = display.DeviceName;
        EnsureLayoutForDisplay();
        _loading = true;
        RefreshAll();
        _loading = false;
        await SaveStateAsync();
    }

    private async void LayoutList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutList.SelectedItem is not LayoutProfile layout || _layout?.Id == layout.Id)
        {
            return;
        }

        StopEngineForEdit();
        _layout = layout;
        _state.ActiveLayoutId = layout.Id;
        RefreshWorkspace();
        await SaveStateAsync();
    }

    private async void AddMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        StopEngineForEdit();
        var dialog = new AddMonitorWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Zone is null)
        {
            return;
        }

        var zone = dialog.Zone;
        var zoneSize = DisplayGeometry.PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);
        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        if (zoneSize.Width > displaySize.Width + 0.01 || zoneSize.Height > displaySize.Height + 0.01)
        {
            MessageBox.Show(this,
                $"A {zone.DiagonalInches:0.#}\" {zone.AspectLabel} monitor is physically larger than the available display area.",
                "Monitor does not fit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FindOpenPosition(zone);
        _layout.Zones.Add(zone);
        RefreshWorkspace();
        WorkspaceCanvas.SelectZone(zone);
        EmptyState.Visibility = Visibility.Collapsed;
        await SaveStateAsync();
    }

    private void FindOpenPosition(VirtualMonitorZone zone)
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        var size = DisplayGeometry.PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);
        const double step = 0.5;

        for (var y = 0.0; y <= Math.Max(0, displaySize.Height - size.Height); y += step)
        {
            for (var x = 0.0; x <= Math.Max(0, displaySize.Width - size.Width); x += step)
            {
                if (!_layout.Zones.Any(existing => IntersectsInches(x, y, size, existing)))
                {
                    zone.XInches = x;
                    zone.YInches = y;
                    return;
                }
            }
        }

        zone.XInches = 0;
        zone.YInches = 0;
    }

    private static bool IntersectsInches(double x, double y, SizeD size, VirtualMonitorZone existing)
    {
        var other = DisplayGeometry.PhysicalSizeFromDiagonal(existing.DiagonalInches, existing.AspectWidth, existing.AspectHeight);
        return x < existing.XInches + other.Width && x + size.Width > existing.XInches &&
               y < existing.YInches + other.Height && y + size.Height > existing.YInches;
    }

    private void EditOnDisplay_Click(object sender, RoutedEventArgs e) => EditOnDisplay();

    private async void EditOnDisplay()
    {
        if (_display is null || _layout is null || _layout.Zones.Count == 0)
        {
            return;
        }

        StopEngineForEdit();
        var backup = StateStore.DeepClone(_layout);
        var overlay = new LayoutOverlayWindow(_display, _layout);
        var saved = overlay.ShowDialog() == true;
        if (!saved)
        {
            _layout.Zones = backup.Zones;
        }

        RefreshWorkspace();
        await SaveStateAsync();
    }

    private async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null)
        {
            return;
        }

        StopEngineForEdit();
        var calibration = new CalibrationWindow(_display);
        if (calibration.ShowDialog() == true)
        {
            foreach (var layout in _state.Layouts.Where(l => l.DisplayDeviceName == _display.DeviceName))
            {
                foreach (var zone in layout.Zones)
                {
                    DisplayGeometry.ClampZoneToDisplay(_display, zone);
                }
            }

            RefreshAll();
            await SaveStateAsync();
        }
    }

    private async void DisplaySizeBox_LostFocus(object sender, RoutedEventArgs e) => await ApplyDisplaySizeAsync(showError: false);

    private async Task<bool> ApplyDisplaySizeAsync(bool showError)
    {
        if (_display is null)
        {
            return false;
        }

        if (!double.TryParse(DisplaySizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var diagonal) || diagonal < 5 || diagonal > 500)
        {
            if (showError)
            {
                MessageBox.Show(this, "Enter a physical display size between 5 and 500 inches.", "Invalid display size", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            DisplaySizeBox.Text = _display.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture);
            return false;
        }

        if (Math.Abs(diagonal - _display.DiagonalInches) < 0.0001)
        {
            return true;
        }

        StopEngineForEdit();
        _display.DiagonalInches = diagonal;
        _display.CalibrationScale = 1.0;
        foreach (var layout in _state.Layouts.Where(l => l.DisplayDeviceName == _display.DeviceName))
        {
            foreach (var zone in layout.Zones)
            {
                DisplayGeometry.ClampZoneToDisplay(_display, zone);
            }
        }

        RefreshAll();
        await SaveStateAsync();
        return true;
    }

    private void WorkspaceCanvas_SelectedZoneChanged(object? sender, VirtualMonitorZone? zone) => RefreshZoneInspector(zone);

    private void RefreshZoneInspector(VirtualMonitorZone? zone)
    {
        if (zone is null)
        {
            ZoneInspector.Visibility = Visibility.Collapsed;
            return;
        }

        ZoneInspector.Visibility = Visibility.Visible;
        ZoneNameBox.Text = zone.Name;
        ZoneSizeBox.Text = zone.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture);
        var target = zone.AspectLabel;
        ZoneAspectBox.SelectedItem = ZoneAspectBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Content?.ToString() == target) ?? ZoneAspectBox.Items[0];

        if (_display is not null)
        {
            var px = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
            ZoneResolutionText.Text = $"Windows monitor mode: {px.Width} × {px.Height} @ 60 Hz";
        }
    }

    private async void UpdateZone_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null || WorkspaceCanvas.SelectedZone is not { } zone)
        {
            return;
        }

        if (!double.TryParse(ZoneSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var diagonal) || diagonal < 5 || diagonal > 500)
        {
            MessageBox.Show(this, "Enter a monitor size between 5 and 500 inches.", "Invalid monitor size", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var aspectText = (ZoneAspectBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "16:9";
        var parts = aspectText.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var aw) || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var ah))
        {
            return;
        }

        var newSize = DisplayGeometry.PhysicalSizeFromDiagonal(diagonal, aw, ah);
        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        if (newSize.Width > displaySize.Width + 0.01 || newSize.Height > displaySize.Height + 0.01)
        {
            MessageBox.Show(this, "That virtual monitor is physically larger than this display.", "Monitor does not fit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StopEngineForEdit();
        zone.Name = string.IsNullOrWhiteSpace(ZoneNameBox.Text) ? "Monitor" : ZoneNameBox.Text.Trim();
        zone.DiagonalInches = diagonal;
        zone.AspectWidth = aw;
        zone.AspectHeight = ah;
        DisplayGeometry.ClampZoneToDisplay(_display, zone);
        RefreshWorkspace();
        WorkspaceCanvas.SelectZone(zone);
        await SaveStateAsync();
    }

    private async void DeleteZone_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || WorkspaceCanvas.SelectedZone is not { } zone)
        {
            return;
        }

        StopEngineForEdit();
        _layout.Zones.RemoveAll(z => z.Id == zone.Id);
        WorkspaceCanvas.SelectZone(null);
        RefreshWorkspace();
        await SaveStateAsync();
    }

    private async void CloneZone_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || WorkspaceCanvas.SelectedZone is not { } source)
        {
            return;
        }

        StopEngineForEdit();
        var clone = new VirtualMonitorZone
        {
            Name = CreateCloneName(source.Name),
            DiagonalInches = source.DiagonalInches,
            AspectWidth = source.AspectWidth,
            AspectHeight = source.AspectHeight
        };

        FindOpenPosition(clone);
        _layout.Zones.Add(clone);
        RefreshWorkspace();
        WorkspaceCanvas.SelectZone(clone);
        await SaveStateAsync();
    }

    private string CreateCloneName(string sourceName)
    {
        if (_layout is null)
        {
            return sourceName + " copy";
        }

        var candidate = sourceName + " copy";
        var suffix = 2;
        var names = _layout.Zones.Select(zone => zone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains(candidate))
        {
            candidate = $"{sourceName} copy {suffix++}";
        }
        return candidate;
    }

    private async void NewLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null)
        {
            return;
        }

        StopEngineForEdit();
        var dialog = new InputWindow("New layout", "Give this workspace a simple name.", "Workspace") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var layout = new LayoutProfile { Name = dialog.Value, DisplayDeviceName = _display.DeviceName };
        _state.Layouts.Add(layout);
        _layout = layout;
        _state.ActiveLayoutId = layout.Id;
        _loading = true;
        RefreshAll();
        _loading = false;
        await SaveStateAsync();
    }

    private async void DeleteLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        var layouts = _state.Layouts.Where(l => l.DisplayDeviceName == _display.DeviceName).ToList();
        if (layouts.Count <= 1)
        {
            MessageBox.Show(this, "Keep at least one layout for each physical display.", "MUX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Delete \"{_layout.Name}\"?", "Delete layout", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        StopEngineForEdit();
        _state.Layouts.RemoveAll(l => l.Id == _layout.Id);
        _layout = _state.Layouts.First(l => l.DisplayDeviceName == _display.DeviceName);
        _state.ActiveLayoutId = _layout.Id;
        _loading = true;
        RefreshAll();
        _loading = false;
        await SaveStateAsync();
    }

    private async void StartupChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = StartupCheck.IsChecked == true;
        try
        {
            _startupService.SetEnabled(enabled);
            _state.LaunchAtStartup = enabled;
        }
        catch (Exception ex)
        {
            StartupCheck.IsChecked = !enabled;
            MessageBox.Show(this, ex.Message, "Unable to change startup setting", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        await SaveStateAsync();
    }

    private bool SafeStartupEnabled()
    {
        try { return _startupService.IsEnabled(); }
        catch { return false; }
    }

    private async void CustomizeShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _state.Shortcuts ??= new ShortcutSettings();
        var previous = StateStore.DeepClone(_state.Shortcuts);
        var dialog = new ShortcutSettingsWindow(_state.Shortcuts) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_hotkeys is not null && !_hotkeys.Reload(dialog.Shortcuts, out var failedIds))
        {
            _hotkeys.Reload(previous, out _);
            MessageBox.Show(this,
                "Windows could not register: " + string.Join(", ", failedIds.Select(ShortcutActionName)) + ". That shortcut may already be used by another app. Your previous shortcuts were restored.",
                "Shortcut unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _state.Shortcuts = dialog.Shortcuts;
        RefreshShortcutSummary();
        await SaveStateAsync();
    }

    private void RefreshShortcutSummary()
    {
        var settings = _state.Shortcuts ?? new ShortcutSettings();
        ShortcutMaximizeText.Text = $"{ShortcutSettingsWindow.Format(settings.ToggleMaximize)}   Maximize / restore";
        ShortcutFullscreenText.Text = $"{ShortcutSettingsWindow.Format(settings.ToggleFullscreen)}   Native fullscreen";
        ShortcutPreviousText.Text = $"{ShortcutSettingsWindow.Format(settings.PreviousMonitor)}   Previous monitor";
        ShortcutNextText.Text = $"{ShortcutSettingsWindow.Format(settings.NextMonitor)}   Next monitor";
        ShortcutEditText.Text = $"{ShortcutSettingsWindow.Format(settings.EditLayout)}   Edit layout";
    }

    private static string ShortcutActionName(int id) => id switch
    {
        1 => "Maximize / restore",
        2 => "Previous monitor",
        3 => "Next monitor",
        4 => "Edit layout",
        5 => "Native fullscreen",
        _ => "Shortcut"
    };

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!await ApplyDisplaySizeAsync(showError: true))
        {
            return;
        }

        if (WorkspaceCanvas.SelectedZone is not null)
        {
            UpdateZone_Click(sender, e);
        }
        else
        {
            await SaveStateAsync();
            StatusText.Text = "Layout saved.";
        }
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
    }

    private Task SaveStateAsync() => _store.SaveAsync(_state);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallDriverButton.IsEnabled = !busy;
        DisplayCombo.IsEnabled = !busy && !_engine.IsRunning;
        LayoutList.IsEnabled = !busy && !_engine.IsRunning;
        RefreshEngineVisual();
    }

    private static string TrimStatus(string text, int max) =>
        string.IsNullOrWhiteSpace(text) ? "No additional details." : text.Length <= max ? text : text[..max] + "…";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyIdReleaseCursor && _engine.IsRunning)
        {
            _engine.ReleaseCursor();
            Activate();
            Topmost = true;
            Topmost = false;
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_hwndSource is not null)
            {
                UnregisterHotKey(_hwndSource.Handle, HotkeyIdReleaseCursor);
                _hwndSource.RemoveHook(WndProc);
            }
        }
        catch { }

        _hotkeys?.Dispose();
        _engine.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
