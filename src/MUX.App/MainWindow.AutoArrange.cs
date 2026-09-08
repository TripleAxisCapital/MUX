using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private static readonly double[] AutoArrangeSizes = { 15, 17, 20, 24, 25, 27, 32, 34, 40, 43 };

    private readonly WindowAutoArrangeService _autoArrangeService = new();
    private bool _autoArrangeControlsInstalled;
    private ComboBox? _autoArrangeSizeBox;
    private TextBlock? _autoArrangeStatusText;
    private DispatcherTimer? _autoArrangeStartupTimer;
    private AutoArrangeCommandBridge? _autoArrangeCommandBridge;
    private bool _autoArrangeLoadedSeen;
    private bool _autoArrangeReady;
    private bool _autoArrangeCommandPending;

    public event EventHandler? AutoArrangeSettingsChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        InitializeAutoArrangeControls();
        _autoArrangeCommandBridge ??= AutoArrangeCommandBridge.Attach(this, QueueAutoArrangeCommand);
    }

    public double AutoArrangeDiagonalInches
        => NormalizeAutoArrangeDiagonal(_state.AutoArrangeDiagonalInches);

    public void InitializeAutoArrangeControls()
    {
        if (_autoArrangeControlsInstalled)
        {
            return;
        }

        _autoArrangeControlsInstalled = true;
        InstallAutoArrangeControls();
        _autoArrangeCommandPending |= AutoArrangeCommandBootstrap.ConsumeStartupRequest();
        Loaded += MainWindow_AutoArrangeLoaded;
        ContentRendered += MainWindow_AutoArrangeContentRendered;
        Closed += MainWindow_AutoArrangeClosed;
    }

    public AutoArrangeResult AutoArrangeCursorDisplay(bool showFeedback = false)
    {
        var result = _autoArrangeService.ArrangeAtCursor(_state.Displays, AutoArrangeDiagonalInches);
        UpdateAutoArrangeStatus(result);

        if (showFeedback && !result.Success)
        {
            MessageBox.Show(
                this,
                result.Message,
                "Auto Arrange",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return result;
    }

    /// <summary>
    /// Entry point used by the global hotkey, Stream Deck launcher and cross-process command bridge.
    /// Commands received during WPF/state startup are held until display calibration has loaded.
    /// </summary>
    public void QueueAutoArrangeCommand()
    {
        _autoArrangeCommandPending = true;

        if (_autoArrangeReady)
        {
            RunPendingAutoArrangeCommand();
            return;
        }

        StartAutoArrangeStartupRefresh();
    }

    public async Task SetAutoArrangeSizeAsync(double diagonalInches)
    {
        var normalized = NormalizeAutoArrangeDiagonal(diagonalInches);
        if (Math.Abs(_state.AutoArrangeDiagonalInches - normalized) < 0.001)
        {
            RefreshAutoArrangeSizeUi();
            return;
        }

        _state.AutoArrangeDiagonalInches = normalized;
        RefreshAutoArrangeSizeUi();
        AutoArrangeSettingsChanged?.Invoke(this, EventArgs.Empty);
        await SaveStateAsync();
    }

    private void InstallAutoArrangeControls()
    {
        if (StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 16, 0, 0),
            Tag = "MUX_AUTO_ARRANGE"
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "AUTO ARRANGE",
            Foreground = new SolidColorBrush(Color.FromRgb(101, 101, 109)),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold
        });

        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });

        _autoArrangeSizeBox = new ComboBox
        {
            ToolTip = "Physical diagonal used for every window in Auto Arrange"
        };
        _autoArrangeSizeBox.SetResourceReference(FrameworkElement.StyleProperty, "MuxComboBox");
        foreach (var size in AutoArrangeSizes)
        {
            _autoArrangeSizeBox.Items.Add(new ComboBoxItem
            {
                Content = $"{size:0.#} in",
                Tag = size
            });
        }
        _autoArrangeSizeBox.SelectionChanged += AutoArrangeSizeBox_SelectionChanged;
        row.Children.Add(_autoArrangeSizeBox);

        var arrangeButton = new Button
        {
            Content = "Arrange",
            Padding = new Thickness(10, 7, 10, 7),
            ToolTip = "Arrange, size and link every normal window on the display under the cursor"
        };
        arrangeButton.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        arrangeButton.Click += AutoArrangeButton_Click;
        Grid.SetColumn(arrangeButton, 2);
        row.Children.Add(arrangeButton);
        stack.Children.Add(row);

        _autoArrangeStatusText = new TextBlock
        {
            Text = "Cursor display · 16:9 · linked after arranging",
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 111)),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 7, 1, 0)
        };
        stack.Children.Add(_autoArrangeStatusText);

        card.Child = stack;
        behaviorPanel.Children.Add(card);
        RefreshAutoArrangeSizeUi();
    }

    private void MainWindow_AutoArrangeLoaded(object sender, RoutedEventArgs e)
    {
        _autoArrangeLoadedSeen = true;

        if (_hotkeys is not null)
        {
            _hotkeys.Register(7, Key.A, QueueAutoArrangeCommand);
        }

        StartAutoArrangeStartupRefresh();
    }

    private void MainWindow_AutoArrangeContentRendered(object? sender, EventArgs e)
    {
        try
        {
            (Application.Current as App)?.InitializeAutoArrangeTrayControls();
        }
        catch
        {
        }
    }

    private void MainWindow_AutoArrangeClosed(object? sender, EventArgs e)
    {
        if (_autoArrangeStartupTimer is not null)
        {
            _autoArrangeStartupTimer.Stop();
            _autoArrangeStartupTimer.Tick -= AutoArrangeStartupTimer_Tick;
            _autoArrangeStartupTimer = null;
        }

        _autoArrangeCommandBridge?.Dispose();
        _autoArrangeCommandBridge = null;
    }

    private void StartAutoArrangeStartupRefresh()
    {
        if (_autoArrangeStartupTimer is not null)
        {
            return;
        }

        _autoArrangeStartupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _autoArrangeStartupTimer.Tick += AutoArrangeStartupTimer_Tick;
        _autoArrangeStartupTimer.Start();
        AutoArrangeStartupTimer_Tick(null, EventArgs.Empty);
    }

    private void AutoArrangeStartupTimer_Tick(object? sender, EventArgs e)
    {
        // MainWindow_Loaded sets _loading before its first await. Requiring that Loaded has actually
        // run as well prevents an early CLI command from racing ahead of state/display discovery.
        if (!_autoArrangeLoadedSeen || _loading)
        {
            return;
        }

        _autoArrangeReady = true;
        RefreshAutoArrangeSizeUi();
        if (_hotkeys is not null)
        {
            _hotkeys.Reload(_state.Shortcuts ?? new MUX.Core.Models.ShortcutSettings(), out _);
        }
        AutoArrangeSettingsChanged?.Invoke(this, EventArgs.Empty);

        if (_autoArrangeStartupTimer is not null)
        {
            _autoArrangeStartupTimer.Stop();
            _autoArrangeStartupTimer.Tick -= AutoArrangeStartupTimer_Tick;
            _autoArrangeStartupTimer = null;
        }

        try
        {
            (Application.Current as App)?.InitializeAutoArrangeTrayControls();
        }
        catch
        {
        }

        RunPendingAutoArrangeCommand();
    }

    private void RunPendingAutoArrangeCommand()
    {
        if (!_autoArrangeReady || !_autoArrangeCommandPending)
        {
            return;
        }

        _autoArrangeCommandPending = false;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => AutoArrangeCursorDisplay(showFeedback: false)));
    }

    private async void AutoArrangeSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _autoArrangeSizeBox?.SelectedItem is not ComboBoxItem { Tag: double size })
        {
            return;
        }

        await SetAutoArrangeSizeAsync(size);
    }

    private void AutoArrangeButton_Click(object sender, RoutedEventArgs e)
    {
        AutoArrangeCursorDisplay(showFeedback: true);
    }

    private void RefreshAutoArrangeSizeUi()
    {
        if (_autoArrangeSizeBox is null)
        {
            return;
        }

        var selected = AutoArrangeDiagonalInches;
        var match = _autoArrangeSizeBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is double size && Math.Abs(size - selected) < 0.001);
        if (match is not null && !ReferenceEquals(_autoArrangeSizeBox.SelectedItem, match))
        {
            _autoArrangeSizeBox.SelectedItem = match;
        }
    }

    private void UpdateAutoArrangeStatus(AutoArrangeResult result)
    {
        if (_autoArrangeStatusText is null)
        {
            return;
        }

        _autoArrangeStatusText.Text = result.Message;
        _autoArrangeStatusText.Foreground = new SolidColorBrush(
            result.Success
                ? Color.FromRgb(142, 180, 152)
                : Color.FromRgb(190, 132, 132));
    }

    private static double NormalizeAutoArrangeDiagonal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 5 || value > 100)
        {
            return 20;
        }

        return Math.Round(value, 2);
    }
}
