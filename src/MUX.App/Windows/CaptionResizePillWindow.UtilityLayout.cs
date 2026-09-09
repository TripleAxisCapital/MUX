using System.Windows;
using System.Windows.Controls;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private const double UtilityCurrentSizeWidth = 46;
    private const double UtilityButtonWidth = 24;
    private const double UtilityGap = 3;
    private const double LegacyUtilityWidth = 92;

    private bool _utilityLayoutInstalled;
    private bool _correctingUtilityWidth;
    private double _utilityClusterWidth = LegacyUtilityWidth;

    /// <summary>
    /// Runs once after every optional pill control has been created. Earlier feature partials used
    /// progressively smaller 13-DIP buttons to stay inside the original utility slot; that is what
    /// caused the clipped/squashed cluster in production. This gives every icon a real hit target,
    /// keeps the physical-size readout legible, and lets FavoritesScroll absorb width pressure.
    /// </summary>
    private void NormalizeUtilityClusterLayout()
    {
        if (MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        var buttons = new List<Button>();
        AddIfPresent(buttons, MagnetButton);
        AddIfPresent(buttons, _edgeCoverButton);
        AddIfPresent(buttons, _edgeCoverTemplateButton);
        AddIfPresent(buttons, _sizeLockButton);
        AddIfPresent(buttons, _windowLinkButton);
        AddIfPresent(buttons, _autoArrangeButton);

        var currentSizeBorder = controlGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => ReferenceEquals(child.Child, CurrentSizeText));

        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityCurrentSizeWidth) });

        if (currentSizeBorder is not null)
        {
            Grid.SetColumn(currentSizeBorder, 0);
            currentSizeBorder.Padding = new Thickness(4, 0, 4, 0);
        }
        CurrentSizeText.FontSize = 9.4;

        for (var index = 0; index < buttons.Count; index++)
        {
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityGap) });
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UtilityButtonWidth) });

            var button = buttons[index];
            button.Width = UtilityButtonWidth;
            button.Height = 34;
            button.Padding = new Thickness(3);
            Grid.SetColumn(button, 2 + index * 2);

            if (button.Content is Viewbox viewbox)
            {
                viewbox.Width = 14;
                viewbox.Height = 14;
            }
        }

        _utilityClusterWidth = UtilityCurrentSizeWidth + buttons.Count * (UtilityGap + UtilityButtonWidth);
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

    private static void AddIfPresent(ICollection<Button> list, Button? button)
    {
        if (button is not null && !list.Contains(button))
        {
            list.Add(button);
        }
    }

    private void UtilityLayout_LayoutModeChanged(object? sender, EventArgs e)
    {
        CorrectCollapsedUtilityWidth();
    }

    private void CorrectCollapsedUtilityWidth()
    {
        if (_correctingUtilityWidth || _expanded || _ratioEditor || _utilityClusterWidth <= LegacyUtilityWidth)
        {
            return;
        }

        var desired = CalculateCollapsedWidth() + (_utilityClusterWidth - LegacyUtilityWidth);
        // Fixed controls must never be compressed. On a narrow target, favorites scroll horizontally
        // first; the utility controls, Size, and ratio controls retain production-sized hit targets.
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
        if (!_utilityLayoutInstalled)
        {
            return;
        }

        LayoutModeChanged -= UtilityLayout_LayoutModeChanged;
        _utilityLayoutInstalled = false;
    }
}
