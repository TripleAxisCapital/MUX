using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MUX.Core.Models;

namespace MUX.App.Services;

public static class ScreenWindowService
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void FillDisplay(Window window, DisplayProfile display)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, display.LeftPx, display.TopPx, display.WidthPx, display.HeightPx, SwpNoZOrder | SwpNoActivate);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
