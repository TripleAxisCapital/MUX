using MUX.Virtual.App.Models;
using System.Windows.Media;

namespace MUX.Virtual.App.Services;

internal sealed class LivePortalVisualSmokeService : IDisposable
{
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private readonly Window _window;
    private bool _closed;

    public LivePortalVisualSmokeService(ActiveVirtualMonitor monitor)
    {
        _window = new Window
        {
            Title = "MUX Live Frame Smoke",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Left = monitor.VirtualRect.Left,
            Top = monitor.VirtualRect.Top,
            Width = monitor.VirtualRect.Width,
            Height = monitor.VirtualRect.Height,
            Background = Brushes.Black,
            Content = null
        };

        _window.Show();

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero ||
            !SetWindowPos(
                hwnd,
                HwndTop,
                monitor.VirtualRect.Left,
                monitor.VirtualRect.Top,
                monitor.VirtualRect.Width,
                monitor.VirtualRect.Height,
                SwpShowWindow))
        {
            var error = Marshal.GetLastWin32Error();
            _window.Close();
            _closed = true;
            throw new InvalidOperationException(
                $"MUX could not place the live-frame smoke window on the virtual monitor ({error}).");
        }
    }

    public async Task<RgbPixel> PaintAndSamplePortalAsync(
        byte red,
        byte green,
        byte blue,
        ScreenRect hostRect,
        CancellationToken cancellationToken = default)
    {
        _window.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        _window.InvalidateVisual();
        _window.UpdateLayout();

        DwmFlush();
        await Task.Delay(700, cancellationToken);
        DwmFlush();

        return SampleScreenPixel(
            hostRect.Left + (hostRect.Width / 2),
            hostRect.Top + (hostRect.Height / 2));
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _window.Close();
            _closed = true;
        }

        GC.SuppressFinalize(this);
    }

    internal static int ColorDistance(RgbPixel left, RgbPixel right) =>
        Math.Abs(left.Red - right.Red) +
        Math.Abs(left.Green - right.Green) +
        Math.Abs(left.Blue - right.Blue);

    internal readonly record struct RgbPixel(byte Red, byte Green, byte Blue)
    {
        public override string ToString() => $"rgb({Red},{Green},{Blue})";
    }

    private static RgbPixel SampleScreenPixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"MUX could not acquire the desktop device context ({Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var color = GetPixel(dc, x, y);
            if (color == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"MUX could not sample the physical live portal at {x},{y}.");
            }

            return new RgbPixel(
                (byte)(color & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)((color >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
