using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private ScreenEdgeBarService? _screenEdgeBarService;
    private CheckBox? _screenEdgeBarsCheck;
    private TextBlock? _screenEdgeBarsStatusText;

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
        Closed += MainWindow_ScreenBarsClosed;
    }

    public bool ToggleScreenEdgeBars()
        => (_screenEdgeBarService ?? ScreenEdgeBarService.Shared).ToggleAllVisibility();

    private void EnsureScreenEdgeBarSettingsControls()
    {
        if (_screenEdgeBarsCheck is not null || StartupCheck.Parent is not StackPanel behaviorPanel)
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

        _screenEdgeBarsCheck = new CheckBox
        {
            Content = "Four screen-edge bars · Disabled"
        };
        _screenEdgeBarsCheck.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
        _screenEdgeBarsCheck.Click += ScreenEdgeBarsCheck_Click;
        behaviorPanel.Children.Add(_screenEdgeBarsCheck);

        _screenEdgeBarsStatusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 111)),
            FontSize = 9,
            Margin = new Thickness(2, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        behaviorPanel.Children.Add(_screenEdgeBarsStatusText);
    }

    internal void RefreshScreenEdgeBarSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshScreenEdgeBarSettings));
            return;
        }

        EnsureScreenEdgeBarSettingsControls();
        if (_screenEdgeBarsCheck is null || _screenEdgeBarsStatusText is null)
        {
            return;
        }

        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        var enabled = _display is not null && service.IsEnabled(_display.DeviceName);
        _screenEdgeBarsCheck.IsChecked = enabled;
        _screenEdgeBarsCheck.Content = enabled
            ? "Four screen-edge bars · Enabled"
            : "Four screen-edge bars · Disabled";

        _screenEdgeBarsStatusText.Text = enabled
            ? service.AreBarsGloballyVisible
                ? "Drag the small handle on each edge to set its depth. The top handle stays at the far left; moving to the rest of the top edge reveals the window caption underneath."
                : "This display's bars are configured but globally hidden. Show them from the MUX tray or your Stream Deck."
            : "Enable independent black bars on all four edges of this physical display. MUX remembers their positions per monitor.";
    }

    private void ScreenBarsDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(RefreshScreenEdgeBarSettings));
    }

    private void ScreenEdgeBarsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _display is null || _screenEdgeBarsCheck is null)
        {
            return;
        }

        var service = _screenEdgeBarService ?? ScreenEdgeBarService.Shared;
        var enabled = _screenEdgeBarsCheck.IsChecked == true;
        service.SetEnabled(_display, enabled);
        service.RefreshNow();
        RefreshScreenEdgeBarSettings();
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

    private void ScreenEdgeBarService_Changed(object? sender, EventArgs e)
    {
        try { RefreshScreenEdgeBarSettings(); } catch { }
    }

    private void MainWindow_ScreenBarsClosed(object? sender, EventArgs e)
    {
        Loaded -= MainWindow_ScreenBarsLoaded;
        Closed -= MainWindow_ScreenBarsClosed;
        DisplayCombo.SelectionChanged -= ScreenBarsDisplayCombo_SelectionChanged;

        if (_screenEdgeBarsCheck is not null)
        {
            _screenEdgeBarsCheck.Click -= ScreenEdgeBarsCheck_Click;
        }

        if (_screenEdgeBarService is not null)
        {
            try { _screenEdgeBarService.Changed -= ScreenEdgeBarService_Changed; } catch { }
            _screenEdgeBarService = null;
        }
    }
}
