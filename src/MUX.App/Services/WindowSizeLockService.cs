using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Pins selected external top-level windows to the exact position and size they had when locked.
/// Interactive move, resize and maximize gestures are blocked before Windows begins moving the HWND;
/// a fast geometry guard remains as a final defence against keyboard/programmatic placement changes.
/// </summary>
public sealed class WindowSizeLockService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int GaRoot = 2;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmNcHitTest = 0x0084;
    private const int WmCancelMode = 0x001F;
    private const int HtCaption = 2;
    private const int HtMaxButton = 9;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint WineventOutOfContext = 0x0000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwRestore = 9;

    private readonly Dictionary<IntPtr, LockedWindow> _lockedWindows = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly WinEventDelegate _winEventProc;
    private IntPtr _mouseHook;
    private IntPtr _moveSizeHook;
    private bool _disposed;

    public WindowSizeLockService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mouseProc = MouseHookProc;
        _winEventProc = WinEventProc;

        // A low-level mouse hook is the only reliable process-external way to prevent a normal
        // caption drag or resize border drag before another application's move loop starts.
        // If Windows refuses the hook for any reason, the WinEvent cancellation + geometry guard
        // below still keep the lock functional instead of making MUX fail to start.
        try
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        }
        catch
        {
            _mouseHook = IntPtr.Zero;
        }

        try
        {
            _moveSizeHook = SetWinEventHook(
                EventSystemMoveSizeStart,
                EventSystemMoveSizeStart,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WineventOutOfContext);
        }
        catch
        {
            _moveSizeHook = IntPtr.Zero;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            // The mouse hook normally means this timer does nothing. The short interval is only a
            // safety net for Win+Arrow, app-driven SetWindowPos, accessibility tools, etc.
            Interval = TimeSpan.FromMilliseconds(12)
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
    /// Toggles the position-and-size lock for a native top-level window. Returns the new state.
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

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && _lockedWindows.Count > 0)
        {
            var message = unchecked((int)wParam.ToInt64());
            if (message is WmLButtonDown or WmLButtonDblClk)
            {
                try
                {
                    var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                    var hitWindow = WindowFromPoint(data.Point);
                    var root = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hitWindow, GaRoot);
                    if (root != IntPtr.Zero && _lockedWindows.ContainsKey(root))
                    {
                        var hit = unchecked((int)SendMessage(
                            root,
                            WmNcHitTest,
                            IntPtr.Zero,
                            MakePointLParam(data.Point.X, data.Point.Y)).ToInt64());

                        if (BlocksGeometryGesture(hit))
                        {
                            // Swallow the mouse message itself: no temporary movement, no snap-back,
                            // and no resize loop ever begins in the target process.
                            return new IntPtr(1);
                        }
                    }
                }
                catch
                {
                    // A hook must never throw across the native callback boundary.
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool BlocksGeometryGesture(int hit)
    {
        return hit == HtCaption ||
               hit == HtMaxButton ||
               hit is >= HtLeft and <= HtBottomRight;
    }

    private void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || eventType != EventSystemMoveSizeStart || hwnd == IntPtr.Zero || !_lockedWindows.ContainsKey(hwnd))
        {
            return;
        }

        // Covers keyboard/system-menu move and size commands that do not originate from the mouse.
        _dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() => CancelInteractiveGeometryChange(hwnd)));
    }

    private void CancelInteractiveGeometryChange(IntPtr hwnd)
    {
        if (_disposed || !_lockedWindows.TryGetValue(hwnd, out var locked))
        {
            return;
        }

        try
        {
            SendMessage(hwnd, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // The geometry guard below is still authoritative.
        }

        locked.Enforce();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _lockedWindows.Count == 0)
        {
            return;
        }

        List<IntPtr>? stale = null;
        foreach (var pair in _lockedWindows.ToArray())
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

        if (_mouseHook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
        }

        if (_moveSizeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_moveSizeHook); } catch { }
            _moveSizeHook = IntPtr.Zero;
        }
    }

    private sealed class LockedWindow
    {
        private readonly IntPtr _hwnd;
        private readonly NativeRect _lockedRect;
        private bool _applying;

        public LockedWindow(IntPtr hwnd, NativeRect lockedRect)
        {
            _hwnd = hwnd;
            _lockedRect = lockedRect;
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
                // Minimize remains allowed. Restoring the window returns it to the locked rect.
                return true;
            }

            _applying = true;
            try
            {
                if (IsZoomed(_hwnd))
                {
                    ShowWindow(_hwnd, SwRestore);
                }

                if (!GetWindowRect(_hwnd, out var current) || current.Width <= 0 || current.Height <= 0)
                {
                    return true;
                }

                if (current.Equals(_lockedRect))
                {
                    return true;
                }

                SetWindowPos(
                    _hwnd,
                    IntPtr.Zero,
                    _lockedRect.Left,
                    _lockedRect.Top,
                    _lockedRect.Width,
                    _lockedRect.Height,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                return true;
            }
            finally
            {
                _applying = false;
            }
        }
    }

    private static IntPtr MakePointLParam(int x, int y)
    {
        var packed = unchecked((y & 0xFFFF) << 16 | (x & 0xFFFF));
        return new IntPtr(packed);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect : IEquatable<NativeRect>
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Equals(NativeRect other)
        {
            return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        }

        public override readonly bool Equals(object? obj) => obj is NativeRect other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHook,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
