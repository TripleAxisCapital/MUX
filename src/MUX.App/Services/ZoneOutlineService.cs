using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

public sealed class ZoneOutlineService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Dictionary<Guid, OutlineVisual> _outlines = new();

    public void Configure(DisplayProfile? display, LayoutProfile? layout, bool enabled, double thickness)
    {
        thickness = Math.Clamp(thickness, 1.0, 6.0);
        if (!enabled || display is null || layout is null || layout.Zones.Count == 0)
        {
            Clear();
            return;
        }

        var activeIds = layout.Zones.Select(zone => zone.Id).ToHashSet();
        foreach (var staleId in _outlines.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            Remove(staleId);
        }

        foreach (var zone in layout.Zones)
        {
            if (!_outlines.TryGetValue(zone.Id, out var visual))
            {
                visual = CreateOutline();
                _outlines[zone.Id] = visual;
            }

            visual.Border.BorderThickness = new Thickness(thickness);
            Position(visual.Window, DisplayGeometry.ZoneToPixels(display, zone));
        }
    }

    private static OutlineVisual CreateOutline()
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };

        var window = new Window
        {
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

        window.SourceInitialized += (_, _) => MakeClickThrough(window);
        window.Show();
        return new OutlineVisual(window, border);
    }

    private static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var desired = current | WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(desired));
    }

    private static void Position(Window window, PixelRect rect)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate | SwpShowWindow);
    }

    private void Remove(Guid id)
    {
        if (!_outlines.Remove(id, out var visual))
        {
            return;
        }

        visual.Window.Close();
    }

    private void Clear()
    {
        foreach (var visual in _outlines.Values)
        {
            visual.Window.Close();
        }

        _outlines.Clear();
    }

    public void Dispose() => Clear();

    private sealed record OutlineVisual(Window Window, Border Border);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

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
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
