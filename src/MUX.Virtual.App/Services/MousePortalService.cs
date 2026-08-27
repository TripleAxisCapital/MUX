using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class MousePortalService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const uint LlmhfInjected = 0x00000001;

    private static readonly IntPtr Zero = IntPtr.Zero;
    private static readonly IntPtr SuppressEvent = new(1);

    private readonly object _gate = new();
    private readonly MouseHookProc _hookProc;
    private IReadOnlyList<ActiveVirtualMonitor> _monitors =
        Array.Empty<ActiveVirtualMonitor>();

    private IntPtr _hook;
    private ActiveVirtualMonitor? _activeMonitor;
    private long _suppressUntil;

    public MousePortalService()
    {
        _hookProc = HookCallback;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start(IReadOnlyList<ActiveVirtualMonitor> monitors)
    {
        Stop();

        _monitors = monitors.ToArray();
        _hook = SetWindowsHookEx(
            WhMouseLl,
            _hookProc,
            GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"MUX could not start the mouse portal ({Marshal.GetLastWin32Error()}).");
        }
    }

    public void ReleaseToHost()
    {
        ActiveVirtualMonitor? current;
        Point point;
        var hasPoint = GetCursorPos(out point);

        lock (_gate)
        {
            current = _activeMonitor;
            if (current is null && hasPoint)
            {
                current = _monitors.FirstOrDefault(x =>
                    x.VirtualRect.Contains(point.X, point.Y));
            }
            _activeMonitor = null;
            _suppressUntil = Environment.TickCount64 + 150;
        }

        if (current is null)
        {
            SetCursorPos(24, 24);
            return;
        }

        var virtualRect = current.VirtualRect;
        var hostRect = current.Plan.HostRect;

        var relativeX = hasPoint
            ? Math.Clamp(point.X - virtualRect.Left, 0, virtualRect.Width - 1)
            : 24;
        var relativeY = hasPoint
            ? Math.Clamp(point.Y - virtualRect.Top, 0, virtualRect.Height - 1)
            : 24;

        SetCursorPos(hostRect.Left + relativeX, hostRect.Top + relativeY);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        lock (_gate)
        {
            _activeMonitor = null;
            _suppressUntil = 0;
        }

        _monitors = Array.Empty<ActiveVirtualMonitor>();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam.ToInt32() != WmMouseMove)
        {
            return CallNextHookEx(Zero, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if ((data.Flags & LlmhfInjected) != 0)
        {
            return CallNextHookEx(Zero, nCode, wParam, lParam);
        }

        ActiveVirtualMonitor? active;
        long suppressUntil;
        lock (_gate)
        {
            active = _activeMonitor;
            suppressUntil = _suppressUntil;
        }

        if (Environment.TickCount64 < suppressUntil)
        {
            return CallNextHookEx(Zero, nCode, wParam, lParam);
        }

        if (active is not null)
        {
            var newVirtualMonitor = _monitors.FirstOrDefault(x =>
                x.VirtualRect.Contains(data.Point.X, data.Point.Y));

            if (newVirtualMonitor is not null)
            {
                lock (_gate)
                {
                    _activeMonitor = newVirtualMonitor;
                }
                return CallNextHookEx(Zero, nCode, wParam, lParam);
            }

            var virtualRect = active.VirtualRect;
            var hostRect = active.Plan.HostRect;
            var relativeX = Math.Clamp(
                data.Point.X - virtualRect.Left,
                0,
                virtualRect.Width - 1);
            var relativeY = Math.Clamp(
                data.Point.Y - virtualRect.Top,
                0,
                virtualRect.Height - 1);

            lock (_gate)
            {
                _activeMonitor = null;
                _suppressUntil = Environment.TickCount64 + 80;
            }

            SetCursorPos(hostRect.Left + relativeX, hostRect.Top + relativeY);
            return SuppressEvent;
        }

        foreach (var monitor in _monitors)
        {
            var host = monitor.Plan.HostRect;
            if (!host.Contains(data.Point.X, data.Point.Y))
            {
                continue;
            }

            var relativeX = data.Point.X - host.Left;
            var relativeY = data.Point.Y - host.Top;
            var virtualX = monitor.VirtualRect.Left +
                Math.Clamp(relativeX, 0, monitor.VirtualRect.Width - 1);
            var virtualY = monitor.VirtualRect.Top +
                Math.Clamp(relativeY, 0, monitor.VirtualRect.Height - 1);

            lock (_gate)
            {
                _activeMonitor = monitor;
                _suppressUntil = Environment.TickCount64 + 80;
            }

            SetCursorPos(virtualX, virtualY);
            return SuppressEvent;
        }

        return CallNextHookEx(Zero, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr MouseHookProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        MouseHookProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
