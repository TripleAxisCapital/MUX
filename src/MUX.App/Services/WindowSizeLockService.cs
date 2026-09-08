using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Keeps selected external top-level windows at the exact pixel size they had when locked.
/// Position changes are allowed; attempts to resize or maximize are immediately restored.
/// </summary>
public sealed class WindowSizeLockService : IDisposable
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwRestore = 9;

    private readonly Dictionary<IntPtr, LockedWindow> _lockedWindows = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public WindowSizeLockService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public event EventHandler? Changed;

    public bool IsLockedForWindow(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && _lockedWindows.ContainsKey(hwnd);
    }

    /// <summary>
    /// Toggles the size lock for a native top-level window. Returns the new locked state.
    /// A maximized or minimized window must be restored before it can be locked.
    /// </summary>
    public bool ToggleWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_lockedWindows.Remove(hwnd))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (!CanLock(hwnd) || !GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        _lockedWindows[hwnd] = new LockedWindow(hwnd, rect);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _lockedWindows.Count == 0)
        {
            return;
        }

        List<IntPtr>? stale = null;
        foreach (var pair in _lockedWindows)
        {
            if (pair.Value.Enforce())
            {
                continue;
            }

            stale ??= new List<IntPtr>();
            stale.Add(pair.Key);
        }

        if (stale is null)
        {
            return;
        }

        foreach (var hwnd in stale)
        {
            _lockedWindows.Remove(hwnd);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool CanLock(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _lockedWindows.Clear();
    }

    private sealed class LockedWindow
    {
        private readonly IntPtr _hwnd;
        private readonly int _lockedWidth;
        private readonly int _lockedHeight;
        private NativeRect _lastRect;
        private bool _applying;

        public LockedWindow(IntPtr hwnd, NativeRect initialRect)
        {
            _hwnd = hwnd;
            _lockedWidth = initialRect.Width;
            _lockedHeight = initialRect.Height;
            _lastRect = initialRect;
        }

        public bool Enforce()
        {
            if (_applying)
            {
                return true;
            }

            if (!IsWindow(_hwnd))
            {
                return false;
            }

            if (!IsWindowVisible(_hwnd) || IsIconic(_hwnd))
            {
                return true;
            }

            // A locked window is meant to stay at its exact current size. If Windows tries to
            // maximize it, restore it to the last normal location and the captured dimensions.
            if (IsZoomed(_hwnd))
            {
                _applying = true;
                try
                {
                    ShowWindow(_hwnd, SwRestore);
                    SetWindowPos(
                        _hwnd,
                        IntPtr.Zero,
                        _lastRect.Left,
                        _lastRect.Top,
                        _lockedWidth,
                        _lockedHeight,
                        SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                    _lastRect = new NativeRect
                    {
                        Left = _lastRect.Left,
                        Top = _lastRect.Top,
                        Right = _lastRect.Left + _lockedWidth,
                        Bottom = _lastRect.Top + _lockedHeight
                    };
                }
                finally
                {
                    _applying = false;
                }

                return true;
            }

            if (!GetWindowRect(_hwnd, out var current) || current.Width <= 0 || current.Height <= 0)
            {
                return true;
            }

            if (current.Width == _lockedWidth && current.Height == _lockedHeight)
            {
                // Normal movement is allowed. Track the new location so a later resize can infer
                // which opposite edge should remain anchored.
                _lastRect = current;
                return true;
            }

            var left = ResolveLockedOrigin(
                current.Left,
                current.Right,
                _lastRect.Left,
                _lastRect.Right,
                _lockedWidth);
            var top = ResolveLockedOrigin(
                current.Top,
                current.Bottom,
                _lastRect.Top,
                _lastRect.Bottom,
                _lockedHeight);

            _applying = true;
            try
            {
                SetWindowPos(
                    _hwnd,
                    IntPtr.Zero,
                    left,
                    top,
                    _lockedWidth,
                    _lockedHeight,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            }
            finally
            {
                _applying = false;
            }

            _lastRect = new NativeRect
            {
                Left = left,
                Top = top,
                Right = left + _lockedWidth,
                Bottom = top + _lockedHeight
            };
            return true;
        }

        private static int ResolveLockedOrigin(
            int currentStart,
            int currentEnd,
            int previousStart,
            int previousEnd,
            int lockedLength)
        {
            var startMovement = Math.Abs(currentStart - previousStart);
            var endMovement = Math.Abs(currentEnd - previousEnd);

            // If the start edge moved more than the end edge, the user is dragging the start
            // (left/top) edge. Keep the opposite edge visually pinned. Otherwise preserve the
            // current start edge, which is correct for right/bottom-edge drags.
            return startMovement > endMovement
                ? currentEnd - lockedLength
                : currentStart;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
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
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
