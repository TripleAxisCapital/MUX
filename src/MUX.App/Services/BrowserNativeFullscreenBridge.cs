using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Bridges MUX Fullscreen into browser-owned fullscreen UI. MUX still owns the
/// outer window bounds; this helper only asks supported browsers to hide their
/// tabs/address bar with their native F11 command.
/// </summary>
public static class BrowserNativeFullscreenBridge
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint VkF11 = 0x7A;
    private const uint MapvkVkToVsc = 0;
    private const int ToggleDelayMs = 180;

    private static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge",
        "chrome",
        "brave",
        "vivaldi",
        "opera",
        "opera_gx",
        "firefox"
    };

    public static IntPtr CaptureSupportedForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return IntPtr.Zero;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return IntPtr.Zero;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return SupportedBrowsers.Contains(process.ProcessName) ? hwnd : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void ToggleAfterShortcutRelease(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(ToggleDelayMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsWindow(hwnd))
            {
                return;
            }

            SendF11(hwnd);
        };

        timer.Start();
    }

    private static void SendF11(IntPtr hwnd)
    {
        var scanCode = MapVirtualKey(VkF11, MapvkVkToVsc);
        var keyDownBits = 1L | ((long)scanCode << 16);
        var keyUpBits = keyDownBits | (1L << 30) | (1L << 31);

        // Target the browser window directly. Delaying until after Ctrl+Alt+F is
        // released prevents Chromium from seeing Ctrl+Alt+F11 instead of F11.
        PostMessage(hwnd, WmKeyDown, new IntPtr(VkF11), new IntPtr(keyDownBits));
        PostMessage(hwnd, WmKeyUp, new IntPtr(VkF11), new IntPtr(keyUpBits));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
