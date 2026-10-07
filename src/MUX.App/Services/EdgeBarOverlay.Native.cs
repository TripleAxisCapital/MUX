using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MUX.App.Services;

internal sealed partial class EdgeBarOverlay
{
    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_disposed || e.ChangedButton != MouseButton.Left || !TryGetCursor(out _dragStart) || !IsInteractivePoint(_dragStart))
        {
            return;
        }

        _dragging = true;
        _pressStartedOnGrip = true;
        _dragStartThickness = _getThickness();
        SetInputTransparent(false);
        CaptureMouse();
        SetHot(true);
        e.Handled = true;
    }

    private void MouseMoveHandler(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !TryGetCursor(out var current))
        {
            return;
        }

        _setThickness(_dragStartThickness + DragDelta(_side, _dragStart, current));
        e.Handled = true;
    }

    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        EndDrag();
        e.Handled = true;
    }

    private void LostCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
        _pressStartedOnGrip = false;
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _pressStartedOnGrip = false;
        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }
    }

    public void Park()
    {
        if (_disposed || !_shown)
        {
            return;
        }

        EndDrag();
        SetInputTransparent(true);
        _cover.BeginAnimation(OpacityProperty, null);
        _cover.Opacity = 1;
        _topBarRevealed = true;
        _lastTopHideUtc = DateTime.MinValue;
        _leftButtonWasDown = false;
        _pressStartedOnGrip = false;
        _hot = false;
        _lastGripOpacity = -1;
        _handle.BeginAnimation(OpacityProperty, null);
        _handle.Opacity = 0;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }

        Opacity = 0;
        _positioned = false;
    }

    private void SetInputTransparent(bool transparent)
    {
        if (_inputTransparent == transparent)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var next = transparent ? style | WsExTransparent : style & ~WsExTransparent;
        if (style != next)
        {
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next));
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);
        }

        _inputTransparent = transparent;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { EndDrag(); } catch { }
        try
        {
            if (IsVisible)
            {
                Close();
            }
        }
        catch { }
    }

    internal static bool TryGetCursor(out PixelPoint point)
    {
        if (GetCursorPos(out var native))
        {
            point = new PixelPoint(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    internal static uint ScaleDpiForWindow(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    internal static uint ScaleDpiForRect(PixelRect rect)
    {
        try
        {
            var nativeRect = new NativeRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var monitor = MonitorFromRect(ref nativeRect, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0)
            {
                return Math.Max(96u, x);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 96u;
    }

    internal static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    private static PixelRect CalculateOverlayRect(PixelRect frame, int thickness, int proximity, EdgeBarSide side)
        => side switch
        {
            EdgeBarSide.Top => new PixelRect(
                frame.Left,
                frame.Top - proximity,
                frame.Right,
                Math.Min(frame.Bottom + proximity, frame.Top + thickness + proximity)),
            EdgeBarSide.Bottom => new PixelRect(
                frame.Left,
                Math.Max(frame.Top - proximity, frame.Bottom - thickness - proximity),
                frame.Right,
                frame.Bottom + proximity),
            EdgeBarSide.Left => new PixelRect(
                frame.Left - proximity,
                frame.Top,
                Math.Min(frame.Right + proximity, frame.Left + thickness + proximity),
                frame.Bottom),
            EdgeBarSide.Right => new PixelRect(
                Math.Max(frame.Left - proximity, frame.Right - thickness - proximity),
                frame.Top,
                frame.Right + proximity,
                frame.Bottom),
            _ => frame
        };

    private static int ClampCenter(int desired, int start, int end, int length, int padding)
    {
        var half = length / 2;
        var min = start + half + padding;
        var max = end - half - padding;
        return min <= max ? Math.Clamp(desired, min, max) : start + Math.Max(0, end - start) / 2;
    }

    private static int DragDelta(EdgeBarSide side, PixelPoint start, PixelPoint current)
        => side switch
        {
            EdgeBarSide.Top => current.Y - start.Y,
            EdgeBarSide.Bottom => start.Y - current.Y,
            EdgeBarSide.Left => current.X - start.X,
            EdgeBarSide.Right => start.X - current.X,
            _ => 0
        };

    private static PixelPoint PointFromLParam(IntPtr lParam)
    {
        var value = unchecked((long)lParam.ToInt64());
        return new PixelPoint(
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

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
