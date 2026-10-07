using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;
using MUX.Core.Models;

namespace MUX.App;

public partial class MainWindow
{
    private ScreenEdgeBarService? _screenEdgeBarService;
    private StackPanel? _screenEdgeBarsPanel;
    private TextBlock? _screenEdgeBarsSummaryText;
    private Button? _screenEdgeBarsVisibilityButton;
    private readonly Dictionary<string, DisplayProfile> _screenEdgeBarDisplays = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshingScreenEdgeBars;

    internal void InitializeScreenEdgeBars()
    {
        if (_screenEdgeBarService is not null)
        {
            return;
        }

        _screenEdgeBarService = ScreenEdgeBarService.Shared;
        _screenEdgeBarService.Changed += ScreenEdgeBarService_Changed;
        EnsureScreenEdgeBarSettingsControls();
        DisplayCombo.SelectionChanged += ScreenBarsDisplayCombo_SelectionChanged;
        Loaded += MainWindow_ScreenBarsLoaded;
        Activated += MainWindow_ScreenBarsActivated;
        Closed += MainWindow_ScreenBarsClosed;
    }

    public bool ToggleScreenEdgeBars()
        => (_screenEdgeBarService ?? ScreenEdgeBarService.Shared).ToggleAllVisibility();

    private void EnsureScreenEdgeBarSettingsControls()
    {
        if (_screenEdgeBarsPanel is not null || StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        behaviorPanel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
            Margin = new Thickness(0, 18, 0, 16)
        });

        behaviorPanel.Children.Add(new TextBlock
        {
            Text = "SCREEN BARS",
            Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 104)),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 8)
        });

        behaviorPanel.Children.Add(new TextBlock
        {
            Text = "Choose exactly which physical displays get independent top, right, bottom and left bars.",
            Foreground = new SolidColorBrush(Color.FromRgb(122, 122, 132)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 10)
        });

        _screenEdgeBarsVisibilityButton = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 7, 10, 7)
        };
        _screenEdgeBarsVisibilityButton.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        _screenEdgeBarsVisibilityButton.Click += ScreenEdgeBarsVisibilityButton_Click;
        behaviorPanel.Children.Add(_screenEdgeBarsVisibilityButton);

        _screenEdgeBarsSummaryText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 111)),
            FontSize = 9,
            Margin = new Thickness(2, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        behaviorPanel.Children.Add(_screenEdgeBarsSummaryText);

        _screenEdgeBarsPanel = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        behaviorPanel.Children.Add(_screenEdgeBarsPanel);

        var bulkButtons = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        bulkButtons.ColumnDefinitions.Add(new ColumnDefinition());
        bulkButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        bulkButtons.ColumnDefinitions.Add(new ColumnDefinition());

        var enableAllButton = new Button
        {
            Content = "Enable All",
            Padding = new Thickness(8, 7, 8, 7)
        };
        enableAllButton.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        enableAllButton.Click += ScreenEdgeBarsEnableAll_Click;
        bulkButtons.Children.Add(enableAllButton);

        var disableAllButton = new Button
        {
            Content = "Disable All",
            Padding = new Thickness(8, 7, 8, 7)
        };
        disableAllButton.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        disableAllButton.Click += ScreenEdgeBarsDisableAll_Click;
        Grid.SetColumn(disableAllButton, 2);
        bulkButtons.Children.Add(disableAllButton);

        behaviorPanel.Children.Add(bulkButtons);
    }

    internal void RefreshScreenEdgeBarSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshScreenEdgeBarSettings));
            return;
        }

        EnsureScreenEdgeBarSettingsControls();
        if (_screenEdgeBarsPanel is null || _screenEdgeBarsSummaryText is null || _screenEdgeBarsVisibilityButton is null)
        {
            return;
        }

        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        var displays = GetScreenEdgeBarDisplays();

        _refreshingScreenEdgeBars = true;
        try
        {
            _screenEdgeBarDisplays.Clear();
            _screenEdgeBarsPanel.Children.Clear();

            var enabledCount = 0;
            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                _screenEdgeBarDisplays[display.DeviceName] = display;

                var enabled = service.IsEnabled(display.DeviceName);
                if (enabled)
                {
                    enabledCount++;
                }

                _screenEdgeBarsPanel.Children.Add(CreateScreenEdgeBarDisplayRow(display, index + 1, enabled));
            }

            if (displays.Count == 0)
            {
                _screenEdgeBarsPanel.Children.Add(new TextBlock
                {
                    Text = "No connected physical displays were detected.",
                    Foreground = new SolidColorBrush(Color.FromRgb(122, 122, 132)),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 2, 0, 0)
                });
            }

            _screenEdgeBarsVisibilityButton.IsEnabled = enabledCount > 0;
            _screenEdgeBarsVisibilityButton.Content = service.AreBarsGloballyVisible
                ? "Hide Enabled Screen Bars"
                : "Show Enabled Screen Bars";

            var visibilityText = service.AreBarsGloballyVisible ? "visible" : "globally hidden";
            _screenEdgeBarsSummaryText.Text = displays.Count == 0
                ? "Connect a display to configure screen-edge bars."
                : $"{enabledCount} of {displays.Count} displays enabled · {visibilityText}. Stream Deck toggles this same global visibility without changing per-screen choices.";
        }
        finally
        {
            _refreshingScreenEdgeBars = false;
        }
    }

    private Border CreateScreenEdgeBarDisplayRow(DisplayProfile display, int displayNumber, bool enabled)
    {
        var label = string.IsNullOrWhiteSpace(display.FriendlyName)
            ? display.DeviceName
            : display.FriendlyName;

        var title = new TextBlock
        {
            Text = $"Display {displayNumber} · {label}",
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var detail = new TextBlock
        {
            Text = display.IsPrimary
                ? $"{display.WidthPx} × {display.HeightPx} · Primary"
                : $"{display.WidthPx} × {display.HeightPx}",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 110)),
            FontSize = 9,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(detail);

        var check = new CheckBox
        {
            Content = text,
            IsChecked = enabled,
            Tag = display.DeviceName,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        check.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
        check.Click += ScreenEdgeBarDisplayCheck_Click;

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Child = check
        };
    }

    private IReadOnlyList<DisplayProfile> GetScreenEdgeBarDisplays()
    {
        try
        {
            var detected = _displayDiscovery.GetDisplays();
            if (detected.Count > 0)
            {
                return detected
                    .GroupBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(display => display.IsPrimary)
                    .ThenBy(display => display.LeftPx)
                    .ThenBy(display => display.TopPx)
                    .ToList();
            }
        }
        catch
        {
        }

        return _state.Displays
            .GroupBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.LeftPx)
            .ThenBy(display => display.TopPx)
            .ToList();
    }

    private void ScreenEdgeBarDisplayCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingScreenEdgeBars || sender is not CheckBox check || check.Tag is not string deviceName)
        {
            return;
        }

        if (!_screenEdgeBarDisplays.TryGetValue(deviceName, out var display))
        {
            RefreshScreenEdgeBarSettings();
            return;
        }

        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        service.SetEnabled(display, check.IsChecked == true);
        service.RefreshNow();
        RefreshScreenEdgeBarSettings();
    }

    private void ScreenEdgeBarsVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        service.ToggleAllVisibility();
        RefreshScreenEdgeBarSettings();
    }

    private void ScreenEdgeBarsEnableAll_Click(object sender, RoutedEventArgs e)
    {
        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        foreach (var display in GetScreenEdgeBarDisplays())
        {
            service.SetEnabled(display, true);
        }

        service.RefreshNow();
        RefreshScreenEdgeBarSettings();
    }

    private void ScreenEdgeBarsDisableAll_Click(object sender, RoutedEventArgs e)
    {
        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        foreach (var display in GetScreenEdgeBarDisplays())
        {
            service.SetEnabled(display, false);
        }

        service.RefreshNow();
        RefreshScreenEdgeBarSettings();
    }

    private void ScreenBarsDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(RefreshScreenEdgeBarSettings));
    }

    private void MainWindow_ScreenBarsLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _screenEdgeBarService?.RefreshNow();
            RefreshScreenEdgeBarSettings();
        }
        catch
        {
        }
    }

    private void MainWindow_ScreenBarsActivated(object? sender, EventArgs e)
    {
        try
        {
            _screenEdgeBarService?.RefreshNow();
            RefreshScreenEdgeBarSettings();
        }
        catch
        {
        }
    }

    private void ScreenEdgeBarService_Changed(object? sender, EventArgs e)
    {
        try { RefreshScreenEdgeBarSettings(); } catch { }
    }

    private void MainWindow_ScreenBarsClosed(object? sender, EventArgs e)
    {
        Loaded -= MainWindow_ScreenBarsLoaded;
        Activated -= MainWindow_ScreenBarsActivated;
        Closed -= MainWindow_ScreenBarsClosed;
        DisplayCombo.SelectionChanged -= ScreenBarsDisplayCombo_SelectionChanged;

        if (_screenEdgeBarsVisibilityButton is not null)
        {
            _screenEdgeBarsVisibilityButton.Click -= ScreenEdgeBarsVisibilityButton_Click;
        }

        if (_screenEdgeBarService is not null)
        {
            try { _screenEdgeBarService.Changed -= ScreenEdgeBarService_Changed; } catch { }
            _screenEdgeBarService = null;
        }
    }
}
