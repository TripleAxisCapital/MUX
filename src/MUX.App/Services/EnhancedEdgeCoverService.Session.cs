using System.Runtime.InteropServices;

namespace MUX.App.Services;

public sealed partial class EnhancedEdgeCoverService
{
    private sealed class CoverSession : IDisposable
    {
        private readonly IntPtr _target;
        private readonly EdgeBarOverlay _top;
        private readonly EdgeBarOverlay _right;
        private readonly EdgeBarOverlay _bottom;
        private readonly EdgeBarOverlay _left;

        private PixelRect _targetRect;
        private int _minimumThickness;
        private int _topThickness;
        private int _rightThickness;
        private int _bottomThickness;
        private int _leftThickness;
        private uint _dpi = 96;
        private bool _initialized;
        private bool _visible = true;
        private bool _disposed;

        public CoverSession(IntPtr target)
        {
            _target = target;
            _dpi = EdgeBarOverlay.ScaleDpiForWindow(target);
            _minimumThickness = EdgeBarOverlay.ScaleForDpi(4, _dpi);
            _topThickness = _minimumThickness;
            _rightThickness = _minimumThickness;
            _bottomThickness = _minimumThickness;
            _leftThickness = _minimumThickness;

            _top = CreateOverlay(EdgeBarSide.Top);
            _right = CreateOverlay(EdgeBarSide.Right);
            _bottom = CreateOverlay(EdgeBarSide.Bottom);
            _left = CreateOverlay(EdgeBarSide.Left);
        }

        private EdgeBarOverlay CreateOverlay(EdgeBarSide side)
            => new(side, () => GetThickness(side), value => SetThickness(side, value));

        public bool Refresh(PixelPoint? cursor, bool visible)
        {
            _visible = visible;
            if (_disposed || !IsWindow(_target))
            {
                return false;
            }

            if (!IsWindowVisible(_target) || IsIconic(_target) || IsWindowCloaked(_target))
            {
                ParkAll();
                return true;
            }

            if (!TryGetTargetRect(_target, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                ParkAll();
                return true;
            }

            _targetRect = rect;
            _dpi = EdgeBarOverlay.ScaleDpiForWindow(_target);
            _minimumThickness = EdgeBarOverlay.ScaleForDpi(4, _dpi);
            _initialized = true;
            ClampThicknesses();

            _top.Update(rect, _topThickness, _dpi, cursor, visible);
            _right.Update(rect, _rightThickness, _dpi, cursor, visible);
            _bottom.Update(rect, _bottomThickness, _dpi, cursor, visible);
            _left.Update(rect, _leftThickness, _dpi, cursor, visible);
            return true;
        }

        public void UpdateInput(PixelPoint cursor, bool visible)
        {
            _top.UpdateInput(cursor, visible);
            _right.UpdateInput(cursor, visible);
            _bottom.UpdateInput(cursor, visible);
            _left.UpdateInput(cursor, visible);
        }

        public bool TryGetGeometry(out EdgeCoverGeometry geometry)
        {
            geometry = default;
            if (_disposed || !_initialized || _targetRect.Width <= 0 || _targetRect.Height <= 0)
            {
                return false;
            }

            geometry = new EdgeCoverGeometry(
                _targetRect.Width,
                _targetRect.Height,
                _minimumThickness,
                _topThickness,
                _rightThickness,
                _bottomThickness,
                _leftThickness);
            return true;
        }

        public bool SetThicknesses(int top, int right, int bottom, int left)
        {
            if (_disposed || !_initialized)
            {
                return false;
            }

            _topThickness = top;
            _rightThickness = right;
            _bottomThickness = bottom;
            _leftThickness = left;
            ClampThicknesses();
            return Refresh(TryCursor(), _visible);
        }

        private int GetThickness(EdgeBarSide side)
            => side switch
            {
                EdgeBarSide.Top => _topThickness,
                EdgeBarSide.Right => _rightThickness,
                EdgeBarSide.Bottom => _bottomThickness,
                EdgeBarSide.Left => _leftThickness,
                _ => _minimumThickness
            };

        private void SetThickness(EdgeBarSide side, int value)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            var maximum = side is EdgeBarSide.Top or EdgeBarSide.Bottom
                ? Math.Max(_minimumThickness, _targetRect.Height)
                : Math.Max(_minimumThickness, _targetRect.Width);
            var next = Math.Clamp(value, _minimumThickness, maximum);

            switch (side)
            {
                case EdgeBarSide.Top: _topThickness = next; break;
                case EdgeBarSide.Right: _rightThickness = next; break;
                case EdgeBarSide.Bottom: _bottomThickness = next; break;
                case EdgeBarSide.Left: _leftThickness = next; break;
            }
        }

        private void ClampThicknesses()
        {
            var verticalMax = Math.Max(_minimumThickness, _targetRect.Height);
            var horizontalMax = Math.Max(_minimumThickness, _targetRect.Width);
            _topThickness = Math.Clamp(_topThickness, _minimumThickness, verticalMax);
            _bottomThickness = Math.Clamp(_bottomThickness, _minimumThickness, verticalMax);
            _leftThickness = Math.Clamp(_leftThickness, _minimumThickness, horizontalMax);
            _rightThickness = Math.Clamp(_rightThickness, _minimumThickness, horizontalMax);
        }

        private void ParkAll()
        {
            _top.Park();
            _right.Park();
            _bottom.Park();
            _left.Park();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SafeDispose(_top);
            SafeDispose(_right);
            SafeDispose(_bottom);
            SafeDispose(_left);
        }
    }

    private static bool TryGetTargetRect(IntPtr hwnd, out PixelRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var native, Marshal.SizeOf<NativeRect>()) == 0)
        {
            rect = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
            if (rect.Width > 0 && rect.Height > 0)
            {
                return true;
            }
        }

        if (GetWindowRect(hwnd, out native))
        {
            rect = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        var cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
