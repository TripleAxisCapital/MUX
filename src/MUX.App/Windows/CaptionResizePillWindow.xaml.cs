using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MUX.App.Windows;

public sealed class CaptionResizeRequestEventArgs : EventArgs
{
    public CaptionResizeRequestEventArgs(IntPtr targetHwnd, int width, int height)
    {
        TargetHwnd = targetHwnd;
        Width = width;
        Height = height;
    }

    public IntPtr TargetHwnd { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Succeeded { get; set; }
}

public partial class CaptionResizePillWindow : Window
{
    private const double CollapsedWidth = 182;
    private const double ExpandedWidth = 322;
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

    public void SetTarget(IntPtr hwnd, int width, int height)
    {
        var targetChanged = hwnd != _targetHwnd;
        _targetHwnd = hwnd;
        UpdateTargetDimensions(width, height, updateEditors: targetChanged || !IsKeyboardFocusWithin);

        if (targetChanged && _expanded)
        {
            SetExpanded(false, focusEditor: false, animate: false);
        }
    }

    public void UpdateTargetDimensions(int width, int height, bool updateEditors = true)
    {
        CurrentSizeText.Text = $"{width:N0} × {height:N0}";

        if (updateEditors)
        {
            WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
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

    private void DimensionBox_PreviewKeyDown(object sender, KeyEventArgs e)
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
        if (_targetHwnd == IntPtr.Zero ||
            !int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width is < 120 or > 32767 ||
            height is < 80 or > 32767)
        {
            WidthBox.ToolTip = "Enter a width from 120 to 32,767 pixels.";
            HeightBox.ToolTip = "Enter a height from 80 to 32,767 pixels.";
            WidthBox.SelectAll();
            WidthBox.Focus();
            return;
        }

        var args = new CaptionResizeRequestEventArgs(_targetHwnd, width, height);
        ResizeRequested?.Invoke(this, args);

        if (!args.Succeeded)
        {
            WidthBox.ToolTip = "Windows did not allow MUX to resize this window.";
            HeightBox.ToolTip = WidthBox.ToolTip;
            WidthBox.SelectAll();
            WidthBox.Focus();
            return;
        }

        UpdateTargetDimensions(width, height);
        SetExpanded(false, focusEditor: false, animate: true);
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
                WidthBox.Focus();
                WidthBox.SelectAll();
                Keyboard.Focus(WidthBox);
            }));
        }
    }
}
