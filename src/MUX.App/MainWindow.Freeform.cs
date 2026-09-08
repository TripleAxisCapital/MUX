using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private DisplayFilteredCaptionPillController? _captionPillController;
    private CheckBox? _predefinedAreasCheck;
    private StackPanel? _captionPillDisplayPanel;
    private bool _freeformControlsInitialized;

    public event EventHandler? PredefinedAreasEnabledChanged;

    public bool PredefinedAreasEnabled => _state.Enabled;

    public void InitializeFreeformControls()
    {
        if (_freeformControlsInitialized)
        {
            return;
        }

        _freeformControlsInitialized = true;
        InstallPredefinedAreasControl();
        InstallCaptionPillDisplayControls();

        EngineToggleButton.ToolTip = "Turn predefined MUX window areas on or off. Exact freeform resizing stays available.";
        EngineToggleButton.Click += EngineToggleButton_FreeformStateChanged;
        Loaded += MainWindow_FreeformLoaded;
        Closed += MainWindow_FreeformClosed;

        _captionPillController = new DisplayFilteredCaptionPillController(
            () => new DisplaySizingSnapshot(_state.Displays, _state.ActiveDisplayDeviceName),
            () => _state.CaptionPillDisabledDisplayDeviceNames ?? Array.Empty<string>());
        _captionPillController.Start();
    }

    public async Task SetPredefinedAreasEnabledAsync(bool enabled)
    {
        if (_state.Enabled != enabled)
        {
            _state.Enabled = enabled;
            RefreshEngineVisual();
            ConfigureWindowManager();
            await SaveStateAsync();
        }

        SyncPredefinedAreasUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InstallPredefinedAreasControl()
    {
        if (StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        _predefinedAreasCheck = new CheckBox
        {
            Content = "Use predefined window areas",
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "Disable this for freeform Windows sizing without deleting your saved MUX layouts."
        };
        _predefinedAreasCheck.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
        _predefinedAreasCheck.Click += PredefinedAreasCheck_Click;

        var snapIndex = behaviorPanel.Children.IndexOf(SnapCheck);
        behaviorPanel.Children.Insert(Math.Max(0, snapIndex), _predefinedAreasCheck);
    }

    private void InstallCaptionPillDisplayControls()
    {
        if (_captionPillDisplayPanel is not null || StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
            Margin = new Thickness(0, 20, 0, 16)
        };
        var heading = new TextBlock
        {
            Text = "PILL DISPLAY",
            Foreground = new SolidColorBrush(Color.FromRgb(101, 101, 109)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 6)
        };
        var description = new TextBlock
        {
            Text = "Choose which physical displays can show the window sizing pill.",
            Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 114)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15,
            Margin = new Thickness(2, 0, 2, 5)
        };
        _captionPillDisplayPanel = new StackPanel();

        var insertIndex = behaviorPanel.Children.IndexOf(StartupCheck) + 1;
        behaviorPanel.Children.Insert(insertIndex++, separator);
        behaviorPanel.Children.Insert(insertIndex++, heading);
        behaviorPanel.Children.Insert(insertIndex++, description);
        behaviorPanel.Children.Insert(insertIndex, _captionPillDisplayPanel);
    }

    private void RefreshCaptionPillDisplayUi()
    {
        if (_captionPillDisplayPanel is null)
        {
            return;
        }

        _state.CaptionPillDisabledDisplayDeviceNames ??= new List<string>();
        _captionPillDisplayPanel.Children.Clear();

        var disabled = _state.CaptionPillDisabledDisplayDeviceNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var display in _state.Displays
                     .OrderByDescending(item => item.IsPrimary)
                     .ThenBy(item => item.TopPx)
                     .ThenBy(item => item.LeftPx))
        {
            var label = display.FriendlyName;
            if (display.IsPrimary)
            {
                label += " · Primary";
            }

            var check = new CheckBox
            {
                Content = label,
                Tag = display.DeviceName,
                IsChecked = !disabled.Contains(display.DeviceName),
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = $"{display.DeviceName} · {display.WidthPx} × {display.HeightPx}"
            };
            check.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
            check.Click += CaptionPillDisplayCheck_Click;
            _captionPillDisplayPanel.Children.Add(check);
        }
    }

    private async void CaptionPillDisplayCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox check || check.Tag is not string deviceName)
        {
            return;
        }

        _state.CaptionPillDisabledDisplayDeviceNames ??= new List<string>();
        _state.CaptionPillDisabledDisplayDeviceNames.RemoveAll(name =>
            name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

        if (check.IsChecked != true)
        {
            _state.CaptionPillDisabledDisplayDeviceNames.Add(deviceName);
        }

        await SaveStateAsync();
    }

    private void MainWindow_FreeformLoaded(object sender, RoutedEventArgs e)
    {
        SyncPredefinedAreasUi();
        RefreshCaptionPillDisplayUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MainWindow_FreeformClosed(object? sender, EventArgs e)
    {
        _captionPillController?.Dispose();
        _captionPillController = null;
    }

    private void EngineToggleButton_FreeformStateChanged(object sender, RoutedEventArgs e)
    {
        SyncPredefinedAreasUi();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void PredefinedAreasCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_predefinedAreasCheck is null)
        {
            return;
        }

        await SetPredefinedAreasEnabledAsync(_predefinedAreasCheck.IsChecked == true);
    }

    private void SyncPredefinedAreasUi()
    {
        if (_predefinedAreasCheck is not null)
        {
            _predefinedAreasCheck.IsChecked = _state.Enabled;
        }

        SnapCheck.IsEnabled = _state.Enabled;
        if (_snapModeBox is not null) _snapModeBox.IsEnabled = _state.Enabled;
        if (_outlineCheck is not null) _outlineCheck.IsEnabled = _state.Enabled;
        if (_outlineThicknessBox is not null) _outlineThicknessBox.IsEnabled = _state.Enabled;

        EngineSubtitle.Text = _state.Enabled ? "Predefined areas on" : "Freeform mode";
    }
}
