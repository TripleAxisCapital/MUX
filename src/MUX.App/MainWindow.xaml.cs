using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MUX.App.Services;
using MUX.App.Windows;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App;

public partial class MainWindow : Window
{
    private readonly StateStore _store = new();
    private readonly DisplayDiscoveryService _displayDiscovery = new();
    private readonly StartupService _startupService = new();
    private readonly WindowManagerService _windowManager = new();
    private HotkeyService? _hotkeys;
    private MuxState _state = new();
    private DisplayProfile? _display;
    private LayoutProfile? _layout;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        WorkspaceCanvas.SelectedZoneChanged += WorkspaceCanvas_SelectedZoneChanged;
        WorkspaceCanvas.LayoutChanged += async (_, _) => await SaveStateAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _state = await _store.LoadAsync();
        MergeDetectedDisplays();
        SelectInitialDisplay();
        RefreshAll();
        _loading = false;
        ConfigureWindowManager();

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotkeyService(this);
        _hotkeys.Register(1, Key.M, () => _windowManager.ToggleForegroundZoneMaximize());
        _hotkeys.Register(2, Key.Left, () => _windowManager.MoveForegroundToAdjacentZone(-1));
        _hotkeys.Register(3, Key.Right, () => _windowManager.MoveForegroundToAdjacentZone(1));
        _hotkeys.Register(4, Key.E, () => Dispatcher.Invoke(EditOnDisplay));
    }

    private void MergeDetectedDisplays()
    {
        var detected = _displayDiscovery.GetDisplays();
        foreach (var current in detected)
        {
            var saved = _state.Displays.FirstOrDefault(d => d.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase));
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

        LayoutTitle.Text = _layout.Name;
        LayoutSubtitle.Text = _layout.Zones.Count == 1 ? "1 virtual monitor" : $"{_layout.Zones.Count} virtual monitors";
        EmptyState.Visibility = _layout.Zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceCanvas.SetModel(_display, _layout);

        SnapCheck.IsChecked = _state.SnapOnDrag;
        StartupCheck.IsChecked = _state.LaunchAtStartup || SafeStartupEnabled();
        RefreshEngineVisual();
        RefreshZoneInspector(null);
    }

    private void RefreshWorkspace()
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        LayoutTitle.Text = _layout.Name;
        LayoutSubtitle.Text = _layout.Zones.Count == 1 ? "1 virtual monitor" : $"{_layout.Zones.Count} virtual monitors";
        EmptyState.Visibility = _layout.Zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceCanvas.SetModel(_display, _layout);
        ConfigureWindowManager();
    }

    private void ConfigureWindowManager()
    {
        _windowManager.Configure(_display, _layout, _state.Enabled, _state.SnapOnDrag);
    }

    private void RefreshEngineVisual()
    {
        EngineSubtitle.Text = _state.Enabled ? "Active" : "Paused";
        EngineDot.Background = new SolidColorBrush(_state.Enabled ? Color.FromRgb(140, 255, 176) : Color.FromRgb(98, 98, 106));
    }

    private async void EngineToggle_Click(object sender, RoutedEventArgs e)
    {
        _state.Enabled = !_state.Enabled;
        RefreshEngineVisual();
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private async void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedItem is not DisplayProfile display || _display?.DeviceName == display.DeviceName)
        {
            return;
        }

        _display = display;
        _state.ActiveDisplayDeviceName = display.DeviceName;
        EnsureLayoutForDisplay();
        _loading = true;
        RefreshAll();
        _loading = false;
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private async void LayoutList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutList.SelectedItem is not LayoutProfile layout || _layout?.Id == layout.Id)
        {
            return;
        }

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
        WorkspaceCanvas.SetModel(_display, _layout);
        WorkspaceCanvas.SelectZone(zone);
        LayoutSubtitle.Text = _layout.Zones.Count == 1 ? "1 virtual monitor" : $"{_layout.Zones.Count} virtual monitors";
        EmptyState.Visibility = Visibility.Collapsed;
        ConfigureWindowManager();
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
                var overlaps = _layout.Zones.Any(existing => IntersectsInches(x, y, size, existing));
                if (!overlaps)
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
            ConfigureWindowManager();
            await SaveStateAsync();
        }
    }

    private async void DisplaySizeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await ApplyDisplaySizeAsync(showError: false);
    }

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
        ConfigureWindowManager();
        await SaveStateAsync();
        return true;
    }

    private void WorkspaceCanvas_SelectedZoneChanged(object? sender, VirtualMonitorZone? zone)
    {
        RefreshZoneInspector(zone);
    }

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
        ZoneAspectBox.SelectedItem = ZoneAspectBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == target)
                                     ?? ZoneAspectBox.Items[0];
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

        zone.Name = string.IsNullOrWhiteSpace(ZoneNameBox.Text) ? "Monitor" : ZoneNameBox.Text.Trim();
        zone.DiagonalInches = diagonal;
        zone.AspectWidth = aw;
        zone.AspectHeight = ah;
        DisplayGeometry.ClampZoneToDisplay(_display, zone);
        WorkspaceCanvas.Refresh();
        WorkspaceCanvas.SelectZone(zone);
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private async void DeleteZone_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || WorkspaceCanvas.SelectedZone is not { } zone)
        {
            return;
        }

        _layout.Zones.RemoveAll(z => z.Id == zone.Id);
        WorkspaceCanvas.SelectZone(null);
        RefreshWorkspace();
        await SaveStateAsync();
    }

    private async void NewLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null)
        {
            return;
        }

        var dialog = new InputWindow("New layout", "Give this workspace a simple name.", "Workspace") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var layout = new LayoutProfile
        {
            Name = dialog.Value,
            DisplayDeviceName = _display.DeviceName
        };
        _state.Layouts.Add(layout);
        _layout = layout;
        _state.ActiveLayoutId = layout.Id;
        _loading = true;
        RefreshAll();
        _loading = false;
        ConfigureWindowManager();
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

        _state.Layouts.RemoveAll(l => l.Id == _layout.Id);
        _layout = _state.Layouts.First(l => l.DisplayDeviceName == _display.DeviceName);
        _state.ActiveLayoutId = _layout.Id;
        _loading = true;
        RefreshAll();
        _loading = false;
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private async void BehaviorChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.SnapOnDrag = SnapCheck.IsChecked == true;
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private async void StartupChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
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
        }
    }

    private Task SaveStateAsync() => _store.SaveAsync(_state);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            await SaveStateAsync();
            Hide();
            return;
        }

        _hotkeys?.Dispose();
        _windowManager.Dispose();
    }
}
