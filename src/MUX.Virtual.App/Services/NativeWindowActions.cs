using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public static class NativeWindowActions
{
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkF11 = 0x7A;

    public static bool ToggleForegroundMaximize()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return ShowWindow(hwnd, IsZoomed(hwnd) ? SwRestore : SwMaximize);
    }

    public static bool ToggleForegroundFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        PostMessage(hwnd, WmKeyDown, new IntPtr(VkF11), IntPtr.Zero);
        PostMessage(hwnd, WmKeyUp, new IntPtr(VkF11), IntPtr.Zero);
        return true;
    }

    public static bool MoveForegroundToAdjacent(
        IReadOnlyList<ActiveVirtualMonitor> monitors,
        int delta)
    {
        if (monitors.Count == 0)
        {
            return false;
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        if (IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
            if (!GetWindowRect(hwnd, out rect))
            {
                return false;
            }
        }

        var centerX = rect.Left + (rect.Right - rect.Left) / 2;
        var centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
        var currentIndex = -1;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].VirtualRect.Contains(centerX, centerY))
            {
                currentIndex = i;
                break;
            }
        }

        var targetIndex = currentIndex < 0
            ? (delta < 0 ? monitors.Count - 1 : 0)
            : (currentIndex + delta + monitors.Count) % monitors.Count;

        var target = monitors[targetIndex].VirtualRect;
        var width = Math.Min(rect.Right - rect.Left, Math.Max(320, target.Width - 80));
        var height = Math.Min(rect.Bottom - rect.Top, Math.Max(200, target.Height - 80));
        var left = target.Left + Math.Max(0, (target.Width - width) / 2);
        var top = target.Top + Math.Max(0, (target.Height - height) / 2);

        return SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
