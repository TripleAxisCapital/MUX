using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private DisplayFilteredCaptionPillController? _captionPillController;
    private CheckBox? _predefinedAreasCheck;
    private StackPanel? _captionPillDisplayPanel;
    private DispatcherTimer? _captionPillDisplayRefreshTimer;
    private string _captionPillDisplaySignature = string.Empty;
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
            () => _state.CaptionPillDisabledDisplayDeviceNames);
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
            Margin = new Thickness(0, 18, 0, 16)
        };
        var heading = new TextBlock
        {
            Text = "PILL DISPLAY",
            Foreground = new SolidColorBrush(Color.FromRgb(101, 101, 109)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 5)
        };
        var description = new TextBlock
        {
            Text = "Choose the monitors where the window pill is allowed to appear.",
            Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 114)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15,
            Margin = new Thickness(2, 0, 2, 7)
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
        if (_captionPillDisplayPanel is null || _loading)
        {
            return;
        }

        // Always refresh Windows' physical display inventory before drawing this section. The old
        // implementation ran before MainWindow's async state load completed, which is why the
        // heading appeared with an empty body.
        var detected = _displayDiscovery.GetDisplays();
        if (detected.Count > 0)
        {
            MergeDetectedDisplays();
        }

        _state.CaptionPillDisabledDisplayDeviceNames ??= new List<string>();
        _captionPillDisplayPanel.Children.Clear();

        var detectedNames = detected.Select(display => display.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displays = _state.Displays
            .Where(display => detected.Count == 0 || detectedNames.Contains(display.DeviceName))
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.TopPx)
            .ThenBy(display => display.LeftPx)
            .ToList();

        _captionPillDisplaySignature = BuildDisplaySignature(displays);

        if (displays.Count == 0)
        {
            _captionPillDisplayPanel.Children.Add(new TextBlock
            {
                Text = "Detecting displays…",
                Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 114)),
                FontSize = 11,
                Margin = new Thickness(2, 7, 0, 4)
            });
            return;
        }

        var disabled = _state.CaptionPillDisabledDisplayDeviceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var display in displays)
        {
            var check = new CheckBox
            {
                Content = display.IsPrimary ? $"{display.FriendlyName} · Primary" : display.FriendlyName,
                Tag = display.DeviceName,
                IsChecked = !disabled.Contains(display.DeviceName),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{display.DeviceName} · {display.WidthPx} × {display.HeightPx}"
            };
            check.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
            check.Click += CaptionPillDisplayCheck_Click;

            var resolution = new TextBlock
            {
                Text = $"{display.WidthPx}×{display.HeightPx}",
                Foreground = new SolidColorBrush(Color.FromRgb(103, 103, 112)),
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsHitTestVisible = false
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(check);
            Grid.SetColumn(resolution, 1);
            grid.Children.Add(resolution);

            _captionPillDisplayPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(38, 38, 43)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 5, 0, 0),
                Child = grid
            });
        }
    }

    private async void CaptionPillDisplayCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox check || check.Tag is not string deviceName)
        {
            return;
        }

        _state.CaptionPillDisabledDisplayDeviceNames ??= new List<string>();
        _state.CaptionPillDisabledDisplayDeviceNames.RemoveAll(name => name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

        if (check.IsChecked != true)
        {
            _state.CaptionPillDisabledDisplayDeviceNames.Add(deviceName);
        }

        await SaveStateAsync();
    }

    private async void MainWindow_FreeformLoaded(object sender, RoutedEventArgs e)
    {
        // MainWindow_Loaded is async and is registered first. Wait for that state/discovery pass to
        // finish before populating monitor choices instead of racing it and rendering an empty list.
        for (var attempt = 0; attempt < 100 && _loading; attempt++)
        {
            await Task.Delay(40);
        }

        SyncPredefinedAreasUi();
        RefreshCaptionPillDisplayUi();
        StartCaptionPillDisplayRefreshTimer();
        PredefinedAreasEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartCaptionPillDisplayRefreshTimer()
    {
        if (_captionPillDisplayRefreshTimer is not null)
        {
            return;
        }

        _captionPillDisplayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _captionPillDisplayRefreshTimer.Tick += CaptionPillDisplayRefreshTimer_Tick;
        _captionPillDisplayRefreshTimer.Start();
    }

    private void CaptionPillDisplayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            var detected = _displayDiscovery.GetDisplays()
                .OrderByDescending(display => display.IsPrimary)
                .ThenBy(display => display.TopPx)
                .ThenBy(display => display.LeftPx)
                .ToList();
            var signature = BuildDisplaySignature(detected);
            if (!signature.Equals(_captionPillDisplaySignature, StringComparison.Ordinal))
            {
                RefreshCaptionPillDisplayUi();
            }
        }
        catch
        {
        }
    }

    private static string BuildDisplaySignature(IEnumerable<MUX.Core.Models.DisplayProfile> displays)
        => string.Join("|", displays.Select(display =>
            $"{display.DeviceName}:{display.LeftPx}:{display.TopPx}:{display.WidthPx}:{display.HeightPx}:{display.IsPrimary}"));

    private void MainWindow_FreeformClosed(object? sender, EventArgs e)
    {
        if (_captionPillDisplayRefreshTimer is not null)
        {
            _captionPillDisplayRefreshTimer.Stop();
            _captionPillDisplayRefreshTimer.Tick -= CaptionPillDisplayRefreshTimer_Tick;
            _captionPillDisplayRefreshTimer = null;
        }

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
