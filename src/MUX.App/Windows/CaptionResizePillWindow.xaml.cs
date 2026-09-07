using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MUX.App.Windows;

public sealed class CaptionResizeRequestEventArgs : EventArgs
{
    public CaptionResizeRequestEventArgs(IntPtr targetHwnd, double diagonalInches)
    {
        TargetHwnd = targetHwnd;
        DiagonalInches = diagonalInches;
    }

    public IntPtr TargetHwnd { get; }
    public double DiagonalInches { get; }
    public int ResultWidth { get; set; }
    public int ResultHeight { get; set; }
    public double ActualDiagonalInches { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
}

public partial class CaptionResizePillWindow : Window
{
    private const double CollapsedWidth = 182;
    private const double ExpandedWidth = 286;
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(105);
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(135);

    private IntPtr _targetHwnd;
    private bool _expanded;
    private long _visibilityAnimationVersion;

    public CaptionResizePillWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<CaptionResizeRequestEventArgs>? ResizeRequested;
    public event EventHandler? LayoutModeChanged;

    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;
    public bool IsInteractionLocked => IsKeyboardFocusWithin;
    public bool IsExpanded => _expanded;

    public void SetTarget(IntPtr hwnd, int width, int height, double? physicalDiagonalInches, string? displayName)
    {
        var targetChanged = hwnd != _targetHwnd;
        _targetHwnd = hwnd;
        UpdateTargetDimensions(width, height, physicalDiagonalInches, displayName, updateEditor: targetChanged || !IsKeyboardFocusWithin);

        if (targetChanged && _expanded)
        {
            SetExpanded(false, focusEditor: false, animate: false);
        }
    }

    public void UpdateTargetDimensions(
        int width,
        int height,
        double? physicalDiagonalInches,
        string? displayName,
        bool updateEditor = true)
    {
        CurrentSizeText.Text = physicalDiagonalInches is > 0
            ? $"{physicalDiagonalInches.Value:0.#}″"
            : "—″";

        CurrentSizeText.ToolTip = physicalDiagonalInches is > 0
            ? $"{width:N0} × {height:N0} px · {physicalDiagonalInches.Value:0.##} in diagonal" +
              (string.IsNullOrWhiteSpace(displayName) ? string.Empty : $" · {displayName}")
            : "MUX needs a configured physical display size to calculate inches.";

        if (updateEditor && physicalDiagonalInches is > 0)
        {
            DiagonalBox.Text = physicalDiagonalInches.Value.ToString("0.##", CultureInfo.CurrentCulture);
        }
    }

    public void Reveal()
    {
        _visibilityAnimationVersion++;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(Opacity, 1.0, RevealDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        var version = ++_visibilityAnimationVersion;
        var animation = new DoubleAnimation(Opacity, 0.0, DismissDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        animation.Completed += (_, _) =>
        {
            if (version != _visibilityAnimationVersion)
            {
                return;
            }

            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
        };

        BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void Collapse(bool animate = true)
    {
        SetExpanded(false, focusEditor: false, animate);
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(true, focusEditor: true, animate: true);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(false, focusEditor: false, animate: true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyRequestedSize();
    }

    private void DiagonalBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyRequestedSize();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetExpanded(false, focusEditor: false, animate: true);
        }
    }

    private void ApplyRequestedSize()
    {
        if (_targetHwnd == IntPtr.Zero || !TryParseDiagonal(DiagonalBox.Text, out var diagonal) || diagonal is < 1 or > 500)
        {
            DiagonalBox.ToolTip = "Enter a physical diagonal from 1 to 500 inches.";
            DiagonalBox.SelectAll();
            DiagonalBox.Focus();
            return;
        }

        var args = new CaptionResizeRequestEventArgs(_targetHwnd, diagonal);
        ResizeRequested?.Invoke(this, args);

        if (!args.Succeeded)
        {
            DiagonalBox.ToolTip = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? "Windows did not allow MUX to resize this window."
                : args.ErrorMessage;
            DiagonalBox.SelectAll();
            DiagonalBox.Focus();
            return;
        }

        UpdateTargetDimensions(
            args.ResultWidth,
            args.ResultHeight,
            args.ActualDiagonalInches > 0 ? args.ActualDiagonalInches : diagonal,
            args.DisplayName);
        SetExpanded(false, focusEditor: false, animate: true);
    }

    private static bool TryParseDiagonal(string text, out double diagonal)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out diagonal) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out diagonal);
    }

    private void SetExpanded(bool expanded, bool focusEditor, bool animate)
    {
        if (_expanded == expanded && (!expanded || !focusEditor))
        {
            return;
        }

        _expanded = expanded;
        CollapsedPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        var targetWidth = expanded ? ExpandedWidth : CollapsedWidth;
        if (!animate || !IsVisible)
        {
            BeginAnimation(WidthProperty, null);
            Width = targetWidth;
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            var start = ActualWidth > 0 ? ActualWidth : Width;
            Width = targetWidth;
            var animation = new DoubleAnimation(start, targetWidth, ResizeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) =>
            {
                BeginAnimation(WidthProperty, null);
                Width = targetWidth;
                LayoutModeChanged?.Invoke(this, EventArgs.Empty);
            };
            BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        LayoutModeChanged?.Invoke(this, EventArgs.Empty);

        if (expanded && focusEditor)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Activate();
                DiagonalBox.Focus();
                DiagonalBox.SelectAll();
                Keyboard.Focus(DiagonalBox);
            }));
        }
    }
}
