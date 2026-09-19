using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace MUX.App.Services;

/// <summary>
/// Non-interactive, click-through snap affordance. It previews only the edge(s)
/// that will attach on mouse-up so dragging remains completely native and smooth.
/// </summary>
internal sealed class SnapPreviewOverlay : IDisposable
{
    private readonly EdgeIndicatorWindow _left = new(vertical: true);
    private readonly EdgeIndicatorWindow _right = new(vertical: true);
    private readonly EdgeIndicatorWindow _top = new(vertical: false);
    private readonly EdgeIndicatorWindow _bottom = new(vertical: false);
    private bool _disposed;

    public void Present(
        int left,
        int top,
        int width,
        int height,
        bool horizontalSnap,
        bool horizontalMovingEdgeIsFar,
        bool verticalSnap,
        bool verticalMovingEdgeIsFar,
        uint dpi)
    {
        if (_disposed || width <= 0 || height <= 0)
        {
            return;
        }

        var line = Math.Max(2, ScaleForDpi(3, dpi));
        var inset = Math.Max(8, ScaleForDpi(10, dpi));

        if (horizontalSnap)
        {
            var x = horizontalMovingEdgeIsFar ? left + width - line : left;
            var y = top + inset;
            var h = Math.Max(line, height - inset * 2);
            if (horizontalMovingEdgeIsFar)
            {
                _left.HideAnimated();
                _right.ShowAt(x, y, line, h);
            }
            else
            {
                _right.HideAnimated();
                _left.ShowAt(x, y, line, h);
            }
        }
        else
        {
            _left.HideAnimated();
            _right.HideAnimated();
        }

        if (verticalSnap)
        {
            var x = left + inset;
            var y = verticalMovingEdgeIsFar ? top + height - line : top;
            var w = Math.Max(line, width - inset * 2);
            if (verticalMovingEdgeIsFar)
            {
                _top.HideAnimated();
                _bottom.ShowAt(x, y, w, line);
            }
            else
            {
                _bottom.HideAnimated();
                _top.ShowAt(x, y, w, line);
            }
        }
        else
        {
            _top.HideAnimated();
            _bottom.HideAnimated();
        }
    }

    public void Dismiss()
    {
        if (_disposed)
        {
            return;
        }

        _left.HideAnimated();
        _right.HideAnimated();
        _top.HideAnimated();
        _bottom.HideAnimated();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _left.Dispose();
        _right.Dispose();
        _top.Dispose();
        _bottom.Dispose();
    }

    private static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    private sealed class EdgeIndicatorWindow : Window, IDisposable
    {
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoOwnerZOrder = 0x0200;

        private readonly ScaleTransform _scale;
        private readonly bool _vertical;
        private bool _shown;
        private bool _visible;
        private bool _positioned;
        private int _x;
        private int _y;
        private int _width;
        private int _height;
        private bool _disposed;

        public EdgeIndicatorWindow(bool vertical)
        {
            _vertical = vertical;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            Topmost = true;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Opacity = 0;

            _scale = vertical
                ? new ScaleTransform(1.0, 0.78)
                : new ScaleTransform(0.78, 1.0);

            var line = new Rectangle
            {
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(235, 242, 242, 247)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = _scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.34,
                    Color = Color.FromRgb(255, 255, 255)
                }
            };

            Content = line;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            style |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        }

        public void ShowAt(int x, int y, int width, int height)
        {
            if (_disposed)
            {
                return;
            }

            if (!_shown)
            {
                Show();
                _shown = true;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (!_positioned || _x != x || _y != y || _width != width || _height != height)
            {
                _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                    SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder);
                _x = x;
                _y = y;
                _width = width;
                _height = height;
                _positioned = true;
            }

            // Do not restart a 105ms animation on every 16ms cursor update.
            // Restarting continuously kept the cue flickering and created a
            // stream of unnecessary native window position changes.
            if (_visible)
            {
                return;
            }

            _visible = true;
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(Opacity, 1.0, TimeSpan.FromMilliseconds(105))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);

            var property = _vertical ? ScaleTransform.ScaleYProperty : ScaleTransform.ScaleXProperty;
            _scale.BeginAnimation(property,
                new DoubleAnimation(_vertical ? _scale.ScaleY : _scale.ScaleX,
                    1.0, TimeSpan.FromMilliseconds(125))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        public void HideAnimated()
        {
            if (_disposed || !_shown || !_visible)
            {
                return;
            }

            _visible = false;
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_shown)
                {
                    Close();
                }
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
