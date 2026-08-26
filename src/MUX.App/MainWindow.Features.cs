using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;
using MUX.App.Windows;
using MUX.Core.Models;

namespace MUX.App;

public partial class MainWindow
{
    private bool _featureControlsInstalled;
    private StackPanel? _shortcutSummaryPanel;
    private MuxFullscreenService? _muxFullscreenService;

    public void InitializeFeatureControls()
    {
        if (_featureControlsInstalled)
        {
            return;
        }

        _featureControlsInstalled = true;
        ApplyDropdownTheme();
        InstallCloneMonitorButton();
        InstallShortcutSettingsButton();

        _muxFullscreenService = new MuxFullscreenService(
            () => _display,
            () => _layout,
            () => _state.Enabled);

        Closed += (_, _) =>
        {
            _muxFullscreenService?.Dispose();
            _muxFullscreenService = null;
        };

        Loaded += FeatureControls_Loaded;
    }

    private async void FeatureControls_Loaded(object sender, RoutedEventArgs e)
    {
        var persisted = await new StateStore().LoadAsync();
        _state.Shortcuts = persisted.Shortcuts ?? new ShortcutSettings();

        if (_hotkeys is not null)
        {
            _hotkeys.Register(5, System.Windows.Input.Key.F, ToggleMuxFullscreen);
            _hotkeys.Reload(_state.Shortcuts, out _);
        }

        RefreshShortcutSummary(_state.Shortcuts);
    }

    private void ToggleMuxFullscreen()
    {
        // Capture the browser before MUX changes its frame. The delayed F11 happens
        // after Ctrl+Alt+F has been released, so Edge/Chrome enter their own true UI
        // fullscreen while MUX continues to own and enforce the virtual-monitor bounds.
        var browserWindow = BrowserNativeFullscreenBridge.CaptureSupportedForegroundWindow();
        _muxFullscreenService?.ToggleForeground();
        BrowserNativeFullscreenBridge.ToggleAfterShortcutRelease(browserWindow);
    }

    private void ApplyDropdownTheme()
    {
        var muted = new SolidColorBrush(Color.FromRgb(153, 153, 163));
        var background = new SolidColorBrush(Color.FromRgb(16, 16, 18));
        var hover = new SolidColorBrush(Color.FromRgb(30, 30, 34));
        var selected = new SolidColorBrush(Color.FromRgb(38, 38, 43));

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, muted));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, background));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, muted));
        itemStyle.Triggers.Add(hoverTrigger);

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, muted));
        itemStyle.Triggers.Add(selectedTrigger);

        System.Windows.Application.Current.Resources[typeof(ComboBoxItem)] = itemStyle;

        DisplayCombo.Foreground = muted;
        ZoneAspectBox.Foreground = muted;
        foreach (var item in ZoneAspectBox.Items.OfType<ComboBoxItem>())
        {
            item.Foreground = muted;
        }
    }

    private void InstallCloneMonitorButton()
    {
        if (ZoneInspector.Children.OfType<Button>().Any(button => Equals(button.Tag, "MUX_CLONE_MONITOR")))
        {
            return;
        }

        var actionGrid = ZoneInspector.Children.OfType<Grid>().LastOrDefault();
        if (actionGrid is null)
        {
            return;
        }

        var cloneButton = new Button
        {
            Content = "Clone monitor",
            Tag = "MUX_CLONE_MONITOR",
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(9, 7, 9, 7)
        };
        cloneButton.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        cloneButton.Click += CloneZone_Click;

        var index = ZoneInspector.Children.IndexOf(actionGrid);
        ZoneInspector.Children.Insert(index + 1, cloneButton);
    }

    private void InstallShortcutSettingsButton()
    {
        if (StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        if (behaviorPanel.Children.OfType<Button>().Any(button => Equals(button.Tag, "MUX_SHORTCUT_SETTINGS")))
        {
            return;
        }

        var button = new Button
        {
            Content = "Customize shortcuts",
            Tag = "MUX_SHORTCUT_SETTINGS",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 8, 12, 8)
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MuxButton");
        button.Click += CustomizeShortcuts_Click;

        var startupIndex = behaviorPanel.Children.IndexOf(StartupCheck);
        behaviorPanel.Children.Insert(startupIndex + 1, button);

        _shortcutSummaryPanel = behaviorPanel.Children
            .OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .FirstOrDefault(panel => panel?.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Keyboard");

        if (_shortcutSummaryPanel is not null && _shortcutSummaryPanel.Children.OfType<TextBlock>().Count() < 6)
        {
            _shortcutSummaryPanel.Children.Insert(2, new TextBlock
            {
                Text = "Ctrl + Alt + F   MUX Fullscreen",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 111)),
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }
    }

    private async void CloneZone_Click(object sender, RoutedEventArgs e)
    {
        if (_display is null || _layout is null || WorkspaceCanvas.SelectedZone is not { } source)
        {
            return;
        }

        var clone = new VirtualMonitorZone
        {
            Name = CreateCloneName(source.Name),
            DiagonalInches = source.DiagonalInches,
            AspectWidth = source.AspectWidth,
            AspectHeight = source.AspectHeight
        };

        FindOpenPosition(clone);
        _layout.Zones.Add(clone);
        WorkspaceCanvas.SetModel(_display, _layout);
        WorkspaceCanvas.SelectZone(clone);
        LayoutSubtitle.Text = _layout.Zones.Count == 1 ? "1 virtual monitor" : $"{_layout.Zones.Count} virtual monitors";
        EmptyState.Visibility = Visibility.Collapsed;
        ConfigureWindowManager();
        await SaveStateAsync();
    }

    private string CreateCloneName(string sourceName)
    {
        if (_layout is null)
        {
            return $"{sourceName} copy";
        }

        var candidate = $"{sourceName} copy";
        var suffix = 2;
        var names = _layout.Zones.Select(zone => zone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains(candidate))
        {
            candidate = $"{sourceName} copy {suffix++}";
        }

        return candidate;
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
            var failedNames = string.Join(", ", failedIds.Select(ShortcutActionName));
            MessageBox.Show(this,
                $"Windows could not register: {failedNames}. That shortcut may already be used by another app. Your previous shortcuts were restored.",
                "Shortcut unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _state.Shortcuts = dialog.Shortcuts;
        RefreshShortcutSummary(_state.Shortcuts);
        await SaveStateAsync();
    }

    private void RefreshShortcutSummary(ShortcutSettings settings)
    {
        if (_shortcutSummaryPanel is null)
        {
            return;
        }

        var lines = _shortcutSummaryPanel.Children.OfType<TextBlock>().ToList();
        if (lines.Count < 6)
        {
            return;
        }

        lines[1].Text = $"{ShortcutSettingsWindow.Format(settings.ToggleMaximize)}   Maximize / restore";
        lines[2].Text = $"{ShortcutSettingsWindow.Format(settings.ToggleFullscreen)}   MUX Fullscreen";
        lines[3].Text = $"{ShortcutSettingsWindow.Format(settings.PreviousMonitor)}   Previous monitor";
        lines[4].Text = $"{ShortcutSettingsWindow.Format(settings.NextMonitor)}   Next monitor";
        lines[5].Text = $"{ShortcutSettingsWindow.Format(settings.EditLayout)}   Edit layout";
    }

    private static string ShortcutActionName(int id) => id switch
    {
        1 => "Maximize / restore",
        2 => "Previous monitor",
        3 => "Next monitor",
        4 => "Edit layout",
        5 => "MUX Fullscreen",
        _ => "Shortcut"
    };
}
