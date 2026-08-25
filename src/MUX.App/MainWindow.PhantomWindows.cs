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

    public void InitializePhantomWindows()
    {
        if (_phantomControlsInstalled)
        {
            return;
        }

        _phantomControlsInstalled = true;
        InstallOutlineControls();
        WorkspaceCanvas.LayoutChanged += (_, _) => _windowManager.RefreshOutlines();
        ZoneOutlineService.LayoutEdited -= ZoneOutlineService_LayoutEdited;
        ZoneOutlineService.LayoutEdited += ZoneOutlineService_LayoutEdited;
        Loaded += PhantomWindows_Loaded;
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
        box.Items.Add(new ComboBoxItem
        {
            Content = label,
            Tag = thickness,
            Foreground = foreground
        });
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
