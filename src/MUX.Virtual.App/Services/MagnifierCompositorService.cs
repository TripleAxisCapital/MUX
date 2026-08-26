using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class MagnifierCompositorService : IDisposable
{
    private const string MagnifierWindowClass = "Magnifier";

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint MsShowMagnifiedCursor = 0x00000001;

    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly WndProcDelegate WndProc = WndProcImpl;
    private static readonly string HostClassName =
        "MUXVirtualPortalHost_" + Environment.ProcessId;

    private readonly List<IntPtr> _hosts = new();
    private ushort _classAtom;
    private bool _magnificationInitialized;

    public void Start(IReadOnlyList<ActiveVirtualMonitor> monitors)
    {
        Stop();

        if (!MagInitialize())
        {
            throw new InvalidOperationException(
                $"Windows Magnification API failed to initialize ({Marshal.GetLastWin32Error()}).");
        }

        _magnificationInitialized = true;
        RegisterHostClass();

        foreach (var monitor in monitors)
        {
            var hostRect = monitor.Plan.HostRect;
            var sourceRect = monitor.VirtualRect;

            var host = CreateWindowEx(
                WsExToolWindow |
                WsExTransparent |
                WsExLayered |
                WsExNoActivate,
                HostClassName,
                string.Empty,
                WsPopup | WsVisible,
                hostRect.Left,
                hostRect.Top,
                hostRect.Width,
                hostRect.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

            if (host == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"MUX could not create a compositor host window ({Marshal.GetLastWin32Error()}).");
            }

            _hosts.Add(host);

            SetLayeredWindowAttributes(host, 0, 255, LwaAlpha);

            var magnifier = CreateWindowEx(
                0,
                MagnifierWindowClass,
                string.Empty,
                WsChild | WsVisible | MsShowMagnifiedCursor,
                0,
                0,
                hostRect.Width,
                hostRect.Height,
                host,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

            if (magnifier == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"MUX could not create a magnifier compositor window ({Marshal.GetLastWin32Error()}).");
            }

            var transform = MagTransform.Identity;
            if (!MagSetWindowTransform(magnifier, ref transform))
            {
                throw new InvalidOperationException(
                    $"MUX could not configure the compositor transform ({Marshal.GetLastWin32Error()}).");
            }

            var rect = new NativeRect
            {
                Left = sourceRect.Left,
                Top = sourceRect.Top,
                Right = sourceRect.Right,
                Bottom = sourceRect.Bottom
            };

            if (!MagSetWindowSource(magnifier, rect))
            {
                throw new InvalidOperationException(
                    $"MUX could not bind the compositor to {monitor.DeviceName} ({Marshal.GetLastWin32Error()}).");
            }

            SetWindowPos(
                host,
                HwndTopmost,
                hostRect.Left,
                hostRect.Top,
                hostRect.Width,
                hostRect.Height,
                SwpNoActivate | SwpShowWindow);
        }
    }

    public void Stop()
    {
        foreach (var host in _hosts)
        {
            if (host != IntPtr.Zero)
            {
                DestroyWindow(host);
            }
        }

        _hosts.Clear();

        if (_classAtom != 0)
        {
            UnregisterClass(HostClassName, GetModuleHandle(null));
            _classAtom = 0;
        }

        if (_magnificationInitialized)
        {
            MagUninitialize();
            _magnificationInitialized = false;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void RegisterHostClass()
    {
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = WndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = HostClassName
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException(
                $"MUX could not register its compositor window class ({Marshal.GetLastWin32Error()}).");
        }
    }

    private static IntPtr WndProcImpl(
        IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam) =>
        DefWindowProc(hwnd, msg, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MagTransform
    {
        public float v00;
        public float v01;
        public float v02;
        public float v10;
        public float v11;
        public float v12;
        public float v20;
        public float v21;
        public float v22;

        public static MagTransform Identity => new()
        {
            v00 = 1,
            v11 = 1,
            v22 = 1
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(
        IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetWindowSource(
        IntPtr hwnd,
        NativeRect rect);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetWindowTransform(
        IntPtr hwnd,
        ref MagTransform transform);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(
        string lpClassName,
        IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
