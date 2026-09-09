using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private Button? _autoArrangeButton;
    private ShapePath? _autoArrangeGlyph;
    private MainWindow? _autoArrangeHost;

    private static readonly Geometry AutoArrangeGeometry = Geometry.Parse(
        "M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z");

    private void InitializeAutoArrangeControls()
    {
        EnsureAutoArrangeButton();
        // Auto Arrange is initialized last, so this is the single authoritative pass that lays out
        // every optional utility control at a usable size instead of progressively squeezing them.
        NormalizeUtilityClusterLayout();
        AttachAutoArrangeHost();
        UpdateAutoArrangeVisual();
    }

    private void EnsureAutoArrangeButton()
    {
        if (_autoArrangeButton is not null || MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        while (controlGrid.ColumnDefinitions.Count < 11)
        {
            if (controlGrid.ColumnDefinitions.Count == 9)
            {
                controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            }
            else
            {
                controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
            }
        }

        if (CollapsedPanel.ColumnDefinitions.Count > 6)
        {
            CollapsedPanel.ColumnDefinitions[6].Width = new GridLength(92);
        }

        _autoArrangeGlyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Data = AutoArrangeGeometry,
            Fill = new SolidColorBrush(Color.FromRgb(214, 214, 220)),
            SnapsToDevicePixels = true
        };

        _autoArrangeButton = new Button
        {
            Width = 13,
            Height = 34,
            Padding = new Thickness(0),
            Content = new Viewbox
            {
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                Child = _autoArrangeGlyph
            }
        };
        _autoArrangeButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        _autoArrangeButton.Click += AutoArrangePillButton_Click;
        _autoArrangeButton.ContextMenu = BuildAutoArrangeSizeMenu();
        Grid.SetColumn(_autoArrangeButton, 10);
        controlGrid.Children.Add(_autoArrangeButton);
    }

    private ContextMenu BuildAutoArrangeSizeMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 31)),
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        foreach (var size in new double[] { 15, 17, 20, 24, 25, 27, 32, 34, 40, 43 })
        {
            var item = new MenuItem
            {
                Header = $"{size:0.#} in",
                Tag = size,
                IsCheckable = true,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 31))
            };
            item.Click += AutoArrangePillSize_Click;
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) => RefreshAutoArrangeSizeMenu(menu);
        return menu;
    }

    private void AttachAutoArrangeHost()
    {
        var main = Application.Current?.MainWindow as MainWindow;
        if (ReferenceEquals(main, _autoArrangeHost))
        {
            return;
        }

        if (_autoArrangeHost is not null)
        {
            _autoArrangeHost.AutoArrangeSettingsChanged -= AutoArrangeHost_SettingsChanged;
        }

        _autoArrangeHost = main;
        if (_autoArrangeHost is not null)
        {
            _autoArrangeHost.AutoArrangeSettingsChanged += AutoArrangeHost_SettingsChanged;
        }
    }

    private void AutoArrangePillButton_Click(object sender, RoutedEventArgs e)
    {
        AttachAutoArrangeHost();
        _autoArrangeHost?.AutoArrangeCursorDisplay();
        UpdateAutoArrangeVisual();
    }

    private async void AutoArrangePillSize_Click(object sender, RoutedEventArgs e)
    {
        AttachAutoArrangeHost();
        if (_autoArrangeHost is null || sender is not MenuItem { Tag: double size })
        {
            return;
        }

        await _autoArrangeHost.SetAutoArrangeSizeAsync(size);
        UpdateAutoArrangeVisual();
    }

    private void AutoArrangeHost_SettingsChanged(object? sender, EventArgs e)
    {
        try { UpdateAutoArrangeVisual(); } catch { }
    }

    private void UpdateAutoArrangeVisual()
    {
        if (_autoArrangeButton is null || _autoArrangeGlyph is null)
        {
            return;
        }

        AttachAutoArrangeHost();
        var size = _autoArrangeHost?.AutoArrangeDiagonalInches ?? 20;
        _autoArrangeButton.ToolTip = $"Auto Arrange · {size:0.#} in · click to arrange cursor display · right-click for size";
        _autoArrangeButton.Background = new SolidColorBrush(Color.FromRgb(31, 31, 35));
        _autoArrangeButton.Opacity = 0.86;
        _autoArrangeGlyph.Fill = new SolidColorBrush(Color.FromRgb(214, 214, 220));
    }

    private void RefreshAutoArrangeSizeMenu(ContextMenu menu)
    {
        var selected = _autoArrangeHost?.AutoArrangeDiagonalInches ?? 20;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is double size && Math.Abs(size - selected) < 0.001;
        }
    }

    private void DisposeAutoArrangeControls()
    {
        DisposeUtilityClusterLayout();

        if (_autoArrangeHost is not null)
        {
            try { _autoArrangeHost.AutoArrangeSettingsChanged -= AutoArrangeHost_SettingsChanged; } catch { }
            _autoArrangeHost = null;
        }

        if (_autoArrangeButton is not null)
        {
            _autoArrangeButton.Click -= AutoArrangePillButton_Click;
        }
    }
}
