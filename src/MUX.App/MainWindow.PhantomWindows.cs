using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;

namespace MUX.App;

public partial class MainWindow
{
    private bool _phantomControlsInstalled;
    private bool _phantomLoading;
    private CheckBox? _outlineCheck;
    private ComboBox? _outlineThicknessBox;
    private FullscreenWindowService? _fullscreenWindowService;

    public void InitializePhantomWindows()
    {
        if (_phantomControlsInstalled)
        {
            return;
        }

        _phantomControlsInstalled = true;
        InstallLegibleFieldStyles();
        InstallOutlineControls();
        WorkspaceCanvas.LayoutChanged += (_, _) => _windowManager.RefreshOutlines();
        ZoneOutlineService.LayoutEdited -= ZoneOutlineService_LayoutEdited;
        ZoneOutlineService.LayoutEdited += ZoneOutlineService_LayoutEdited;

        _fullscreenWindowService = new FullscreenWindowService(
            () => _display,
            () => _layout,
            () => _state.Enabled);

        Closed += (_, _) =>
        {
            ZoneOutlineService.LayoutEdited -= ZoneOutlineService_LayoutEdited;
            _fullscreenWindowService?.Dispose();
            _fullscreenWindowService = null;
        };

        Loaded += PhantomWindows_Loaded;
    }

    private static void InstallLegibleFieldStyles()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        InstallImplicitControlStyle<TextBox>(app, "MuxTextBox", "#151518", "#D1D1D6", "#35353B");
        InstallImplicitControlStyle<ComboBox>(app, "MuxComboBox", "#151518", "#B8B8C0", "#35353B");
        InstallImplicitControlStyle<ComboBoxItem>(app, "MuxComboBoxItem", "#151518", "#B8B8C0", "#151518");
    }

    private static void InstallImplicitControlStyle<T>(
        System.Windows.Application app,
        string baseStyleKey,
        string backgroundHex,
        string foregroundHex,
        string borderHex)
        where T : Control
    {
        if (app.TryFindResource(baseStyleKey) is not Style baseStyle)
        {
            return;
        }

        var style = new Style(typeof(T), baseStyle);
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundHex)!)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(foregroundHex)!)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderHex)!)));
        app.Resources[typeof(T)] = style;
    }

    private void InstallOutlineControls()
    {
        if (StartupCheck.Parent is not StackPanel behaviorPanel)
        {
            return;
        }

        var muted = new SolidColorBrush(Color.FromRgb(153, 153, 163));
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };

        _outlineCheck = new CheckBox
        {
            Content = "Show monitor outlines"
        };
        _outlineCheck.SetResourceReference(FrameworkElement.StyleProperty, "MuxCheckBox");
        _outlineCheck.Click += OutlineVisibilityChanged_Click;
        panel.Children.Add(_outlineCheck);

        panel.Children.Add(new TextBlock
        {
            Text = "Default outline thickness",
            Foreground = muted,
            FontSize = 11,
            Margin = new Thickness(2, 12, 0, 7)
        });

        _outlineThicknessBox = new ComboBox
        {
            Foreground = muted
        };
        _outlineThicknessBox.SetResourceReference(FrameworkElement.StyleProperty, "MuxComboBox");
        AddThicknessOption(_outlineThicknessBox, "1 px · Hairline", 1.0, muted);
        AddThicknessOption(_outlineThicknessBox, "2 px · Standard", 2.0, muted);
        AddThicknessOption(_outlineThicknessBox, "3 px · Bold", 3.0, muted);
        AddThicknessOption(_outlineThicknessBox, "4 px · Heavy", 4.0, muted);
        _outlineThicknessBox.SelectionChanged += OutlineThickness_SelectionChanged;
        panel.Children.Add(_outlineThicknessBox);

        var snapIndex = behaviorPanel.Children.IndexOf(SnapCheck);
        behaviorPanel.Children.Insert(Math.Max(0, snapIndex + 1), panel);
    }

    private static void AddThicknessOption(ComboBox box, string label, double thickness, Brush foreground)
    {
        var item = new ComboBoxItem
        {
            Content = label,
            Tag = thickness,
            Foreground = foreground,
            Background = new SolidColorBrush(Color.FromRgb(21, 21, 24))
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MuxComboBoxItem");
        box.Items.Add(item);
    }

    private void PhantomWindows_Loaded(object sender, RoutedEventArgs e)
    {
        _phantomLoading = true;
        try
        {
            _state.ZoneOutlineThickness = _state.ZoneOutlineThickness is >= 1.0 and <= 6.0
                ? _state.ZoneOutlineThickness
                : 2.0;

            if (_outlineCheck is not null)
            {
                _outlineCheck.IsChecked = _state.ShowZoneOutlines;
            }

            if (_outlineThicknessBox is not null)
            {
                _outlineThicknessBox.SelectedItem = _outlineThicknessBox.Items
                    .OfType<ComboBoxItem>()
                    .OrderBy(item => Math.Abs(((double)item.Tag) - _state.ZoneOutlineThickness))
                    .FirstOrDefault();
            }
        }
        finally
        {
            _phantomLoading = false;
        }

        ApplyOutlineOptions();
    }

    private async void OutlineVisibilityChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_phantomLoading)
        {
            return;
        }

        _state.ShowZoneOutlines = _outlineCheck?.IsChecked == true;
        ApplyOutlineOptions();
        await SaveStateAsync();
    }

    private async void OutlineThickness_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_phantomLoading || _outlineThicknessBox?.SelectedItem is not ComboBoxItem { Tag: double thickness })
        {
            return;
        }

        _state.ZoneOutlineThickness = thickness;
        ApplyOutlineOptions();
        await SaveStateAsync();
    }

    private async void ZoneOutlineService_LayoutEdited(object? sender, EventArgs e)
    {
        if (_loading || _display is null || _layout is null)
        {
            return;
        }

        RefreshWorkspace();
        await SaveStateAsync();
    }

    private void ApplyOutlineOptions()
    {
        _windowManager.SetOutlineOptions(_state.ShowZoneOutlines, _state.ZoneOutlineThickness);
    }
}
