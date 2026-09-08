using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Gates the caption sizing pill by physical display. Disabled displays never keep a live pill
/// service, so the helper cannot leak onto a monitor through overlap, fallback sizing, or a stale
/// target window. The gate is intentionally faster than the pill's own hover timer so transitions
/// between enabled and disabled displays feel immediate.
/// </summary>
public sealed class DisplayFilteredCaptionPillController : IDisposable
{
    private readonly Func<DisplaySizingSnapshot> _sizingProvider;
    private readonly Func<IEnumerable<string>> _disabledDisplayProvider;
    private readonly DispatcherTimer _timer;
    private CaptionResizePillService? _service;
    private string _lastDeviceName = string.Empty;
    private bool _lastAllowed;
    private bool _started;
    private bool _disposed;

    public DisplayFilteredCaptionPillController(
        Func<DisplaySizingSnapshot> sizingProvider,
        Func<IEnumerable<string>> disabledDisplayProvider)
    {
        _sizingProvider = sizingProvider;
        _disabledDisplayProvider = disabledDisplayProvider;
        _timer = new DispatcherTimer(DispatcherPriority.Input, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(24)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Evaluate(force: true);
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Evaluate(force: false);

    private void Evaluate(bool force)
    {
        if (_disposed || !GetCursorPos(out var cursor))
        {
            return;
        }

        DisplaySizingSnapshot snapshot;
        try
        {
            snapshot = _sizingProvider() ?? new DisplaySizingSnapshot(Array.Empty<DisplayProfile>(), string.Empty);
        }
        catch
        {
            snapshot = new DisplaySizingSnapshot(Array.Empty<DisplayProfile>(), string.Empty);
        }

        var display = ResolveDisplayAtPoint(snapshot.Displays, cursor.X, cursor.Y);
        var deviceName = display?.DeviceName ?? string.Empty;
        var allowed = display is null || !IsDisabled(deviceName);

        if (!force &&
            allowed == _lastAllowed &&
            deviceName.Equals(_lastDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastAllowed = allowed;
        _lastDeviceName = deviceName;

        if (allowed)
        {
            EnsureService();
        }
        else
        {
            StopService();
        }
    }

    private bool IsDisabled(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        try
        {
            return (_disabledDisplayProvider() ?? Array.Empty<string>())
                .Any(name => name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void EnsureService()
    {
        if (_service is not null)
        {
            return;
        }

        try
        {
            _service = new CaptionResizePillService(_sizingProvider);
            _service.Start();
        }
        catch
        {
            try { _service?.Dispose(); } catch { }
            _service = null;
        }
    }

    private void StopService()
    {
        if (_service is null)
        {
            return;
        }

        try { _service.Dispose(); } catch { }
        _service = null;
    }

    private static DisplayProfile? ResolveDisplayAtPoint(IReadOnlyList<DisplayProfile> displays, int x, int y)
    {
        foreach (var display in displays)
        {
            if (display.WidthPx <= 0 || display.HeightPx <= 0)
            {
                continue;
            }

            if (x >= display.LeftPx &&
                x < display.LeftPx + display.WidthPx &&
                y >= display.TopPx &&
                y < display.TopPx + display.HeightPx)
            {
                return display;
            }
        }

        return null;
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
        StopService();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
