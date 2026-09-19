using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    // Keep frequent actions in the pill, with the remaining controls in a clean
    // secondary panel. The original buttons are moved, not recreated: all handlers,
    // context menus, state indicators, and feature services remain intact.
    private const double UtilityCurrentSizeWidth = 46;
    private const double UtilityButtonWidth = 32;
    private const double UtilityGap = 4;
    private const double LegacyUtilityWidth = 92;

    private bool _utilityLayoutInstalled;
    private bool _correctingUtilityWidth;
    private double _utilityClusterWidth = LegacyUtilityWidth;
    private Button? _moreButton;
    private Popup? _utilityPopup;
    private DateTime _lastAdvancedHoverUtc = DateTime.MinValue;

    private void NormalizeUtilityClusterLayout()
    {
        if (MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        var currentSizeBorder = controlGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => ReferenceEquals(child.Child, CurrentSizeText));

        var primaryButtons = new List<Button> { MagnetButton };
        if (_edgeCoverButton is not null)
        {
            primaryButtons.Add(_edgeCoverButton);
        }

        InstallAdvancedControls(controlGrid);
        if (_moreButton is not null)
        {
            primaryButtons.Add(_moreButton);
        }

        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityCurrentSizeWidth) });
        if (currentSizeBorder is not null)
        {
            Grid.SetColumn(currentSizeBorder, 0);
            currentSizeBorder.Padding = new Thickness(4, 0, 4, 0);
        }

        CurrentSizeText.FontSize = 9.8;
        for (var index = 0; index < primaryButtons.Count; index++)
        {
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityGap) });
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityButtonWidth) });

            var button = primaryButtons[index];
            button.Width = UtilityButtonWidth;
            button.Height = 34;
            button.Padding = new Thickness(4);
            Grid.SetColumn(button, 2 + index * 2);
            if (button.Content is Viewbox viewbox)
            {
                viewbox.Width = 15;
                viewbox.Height = 15;
            }
        }

        _utilityClusterWidth = UtilityCurrentSizeWidth + primaryButtons.Count * (UtilityGap + UtilityButtonWidth);
        if (CollapsedPanel.ColumnDefinitions.Count > 6)
        {
            CollapsedPanel.ColumnDefinitions[6].Width = new GridLength(_utilityClusterWidth);
            CollapsedPanel.ColumnDefinitions[6].MinWidth = _utilityClusterWidth;
        }
        controlGrid.MinWidth = _utilityClusterWidth;
        controlGrid.HorizontalAlignment = HorizontalAlignment.Right;

        if (!_utilityLayoutInstalled)
        {
            _utilityLayoutInstalled = true;
            LayoutModeChanged += UtilityLayout_LayoutModeChanged;
        }
        CorrectCollapsedUtilityWidth();
    }

    private void InstallAdvancedControls(Grid controlGrid)
    {
        if (_moreButton is not null)
        {
            return;
        }

        var advanced = new (Button? Button, string Label)[]
        {
            (_edgeCoverTemplateButton, "Save black-bar template"),
            (_sizeLockButton, "Lock window size"),
            (_windowLinkButton, "Link windows"),
            (_autoArrangeButton, "Auto arrange")
        };
        if (advanced.All(item => item.Button is null))
        {
            return;
        }

        var panel = new StackPanel();
        foreach (var (button, label) in advanced)
        {
            if (button is null)
            {
                continue;
            }

            if (button.Parent is Panel previous)
            {
                previous.Children.Remove(button);
            }

            button.Width = 34;
            button.Height = 34;
            button.Padding = new Thickness(5);
            button.Margin = new Thickness(0, 0, 10, 0);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(button);
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 226, 231)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(row);
        }

        _moreButton = new Button
        {
            Content = "···",
            ToolTip = "More window controls",
            Width = UtilityButtonWidth,
            Height = 34,
            Padding = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        _moreButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        controlGrid.Children.Add(_moreButton);

        _utilityPopup = new Popup
        {
            PlacementTarget = _moreButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 9,
            AllowsTransparency = true,
            // Do not capture or discard clicks intended for the underlying application.
            StaysOpen = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(10),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.35,
                    Color = Colors.Black
                },
                Child = panel
            }
        };

        // StaysOpen=false may dismiss the popup before Button.Click; handle a
        // second click explicitly so the More button reliably closes the panel.
        _moreButton.Click += MoreButton_Click;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_utilityPopup is not null)
        {
            _utilityPopup.IsOpen = !_utilityPopup.IsOpen;
            _lastAdvancedHoverUtc = DateTime.UtcNow;
        }
    }

    public void RefreshAdvancedActionsHover(DateTime now)
    {
        if (_utilityPopup?.IsOpen != true) return;
        if (_utilityPopup.IsMouseOver || IsMouseOver || _moreButton?.IsMouseOver == true)
            _lastAdvancedHoverUtc = now;
        else if (now - _lastAdvancedHoverUtc >= TimeSpan.FromMilliseconds(240))
            _utilityPopup.IsOpen = false;
    }

    private void UtilityLayout_LayoutModeChanged(object? sender, EventArgs e)
        => CorrectCollapsedUtilityWidth();

    private void CorrectCollapsedUtilityWidth()
    {
        if (_correctingUtilityWidth || _expanded || _ratioEditor || _utilityClusterWidth <= LegacyUtilityWidth)
        {
            return;
        }

        var desired = CalculateCollapsedWidth() + (_utilityClusterWidth - LegacyUtilityWidth);
        var fixedFunctionalWidth = 16 + 58 + 6 + 58 + 6 + 6 + _utilityClusterWidth;
        var available = double.IsFinite(_availableWidthDip)
            ? Math.Max(_availableWidthDip, fixedFunctionalWidth)
            : desired;
        var constrained = Math.Max(MinimumPillWidth, Math.Min(desired, available));
        if (Math.Abs(Width - constrained) < 0.5)
        {
            return;
        }

        _correctingUtilityWidth = true;
        try
        {
            BeginAnimation(WidthProperty, null);
            Width = constrained;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _correctingUtilityWidth = false;
        }
    }

    private void DisposeUtilityClusterLayout()
    {
        _utilityPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        if (_moreButton is not null)
        {
            _moreButton.Click -= MoreButton_Click;
        }
        _utilityPopup = null;
        _moreButton = null;
        if (_utilityLayoutInstalled)
        {
            LayoutModeChanged -= UtilityLayout_LayoutModeChanged;
            _utilityLayoutInstalled = false;
        }
    }
}
