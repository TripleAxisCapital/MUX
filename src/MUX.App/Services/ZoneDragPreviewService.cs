using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Transient, click-through snap targets. Nothing is shown at rest.
/// WinEvent move gestures own visibility; no global mouse hooks are installed.
/// </summary>
internal sealed class ZoneDragPreviewService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly Dictionary<Guid, (Window Window, Border Border)> _targets = new();
    private bool _disposed;

    public void Render(DisplayProfile display, LayoutProfile layout, Guid? hovered, string highlightColor, double thickness)
    {
        if (_disposed) return;
        var chosen = ParseColor(highlightColor);
        var active = layout.Zones.Select(z => z.Id).ToHashSet();
        foreach (var id in _targets.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _targets[id].Window.Close();
            _targets.Remove(id);
        }

        foreach (var zone in layout.Zones)
        {
            if (!_targets.TryGetValue(zone.Id, out var entry))
            {
                entry = CreateTarget();
                _targets.Add(zone.Id, entry);
            }

            var isHovered = zone.Id == hovered;
            entry.Border.BorderBrush = new SolidColorBrush(
                isHovered ? chosen : Color.FromArgb(175, 206, 206, 206));
            entry.Border.BorderThickness = new Thickness(isHovered ? Math.Max(3, thickness + 1) : 1.3);
            entry.Border.Background = isHovered
                ? new SolidColorBrush(Color.FromArgb(34, chosen.R, chosen.G, chosen.B))
                : new SolidColorBrush(Color.FromArgb(7, 220, 220, 220));

            var rect = DisplayGeometry.ZoneToPixels(display, zone);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            if (!entry.Window.IsVisible) entry.Window.Show();
            var hwnd = new WindowInteropHelper(entry.Window).Handle;
            SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height,
                SwpNoActivate | SwpShowWindow);
        }
    }

    public void Hide()
    {
        foreach (var target in _targets.Values)
        {
            if (target.Window.IsVisible) target.Window.Hide();
        }
    }

    public static Color ParseColor(string? value)
    {
        if (value is { Length: 7 } && value[0] == '#' &&
            byte.TryParse(value.Substring(1, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(value.Substring(3, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(value.Substring(5, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var b))
            return Color.FromRgb(r, g, b);
        return Color.FromRgb(48, 209, 88);
    }

    private static (Window Window, Border Border) CreateTarget()
    {
        var border = new Border { CornerRadius = new CornerRadius(2), IsHitTestVisible = false, SnapsToDevicePixels = true };
        var window = new Window
        {
            Width = 100,
            Height = 100,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            Content = border
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle,
                new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        };
        return (window, border);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _targets.Values) item.Window.Close();
        _targets.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value));
}
