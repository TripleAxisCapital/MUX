using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Per-window black edge covers. The four bars track the native extended frame bounds and share
/// one input policy: every black surface is click-through except its small resize grip. The top
/// grip is deliberately kept at the far-left edge; approaching the rest of the top caption hides
/// the cover so the underlying window can always be grabbed and dragged normally.
/// </summary>
public readonly record struct EdgeCoverGeometry(
    int Width, int Height, int MinimumThickness,
    int TopThickness, int RightThickness, int BottomThickness, int LeftThickness);

public sealed partial class EnhancedEdgeCoverService : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private static readonly Lazy<EnhancedEdgeCoverService> SharedInstance = new(() => new EnhancedEdgeCoverService());

    private readonly Dictionary<IntPtr, CoverSession> _sessions = new();
    private readonly DispatcherTimer _geometryTimer;
    private readonly DispatcherTimer _inputTimer;
    private bool _globallyVisible = true;
    private bool _disposed;

    public EnhancedEdgeCoverService()
    {
        _geometryTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };
        _geometryTimer.Tick += GeometryTimer_Tick;
        _geometryTimer.Start();

        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inputTimer.Tick += InputTimer_Tick;
        _inputTimer.Start();
    }

    public static EnhancedEdgeCoverService Shared => SharedInstance.Value;

    public event EventHandler? Changed;

    public bool AreCoversGloballyVisible => _globallyVisible;
    public int ConfiguredWindowCount => _sessions.Count;

    public bool IsEnabledForWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _sessions.ContainsKey(hwnd);

    public bool TryGetCoverGeometry(IntPtr hwnd, out EdgeCoverGeometry geometry)
    {
        geometry = default;
        return !_disposed && _sessions.TryGetValue(hwnd, out var session) && session.TryGetGeometry(out geometry);
    }

    public bool TrySetCoverThicknesses(IntPtr hwnd, int top, int right, int bottom, int left)
    {
        if (_disposed || !_sessions.TryGetValue(hwnd, out var session) ||
            !session.SetThicknesses(top, right, bottom, left))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    public bool ToggleAllVisibility()
    {
        if (_disposed)
        {
            return false;
        }

        SetAllVisibility(!_globallyVisible);
        return _globallyVisible;
    }

    public void SetAllVisibility(bool visible)
    {
        if (_disposed || _globallyVisible == visible)
        {
            return;
        }

        _globallyVisible = visible;
        RefreshAll();
        RaiseChanged();
    }

    public bool ToggleWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_sessions.Remove(hwnd, out var existing))
        {
            SafeDispose(existing);
            RaiseChanged();
            return false;
        }

        if (!IsEligibleTarget(hwnd))
        {
            return false;
        }

        CoverSession? session = null;
        try
        {
            session = new CoverSession(hwnd);
            if (!session.Refresh(TryCursor(), _globallyVisible))
            {
                SafeDispose(session);
                return false;
            }

            _sessions[hwnd] = session;
            RaiseChanged();
            return true;
        }
        catch
        {
            SafeDispose(session);
            return false;
        }
    }

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0 || TryCursor() is not PixelPoint point)
        {
            return;
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            try { session.UpdateInput(point, _globallyVisible); } catch { }
        }
    }

    private void GeometryTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0)
        {
            return;
        }

        RefreshAll();
    }

    private void RefreshAll()
    {
        var cursor = TryCursor();
        List<IntPtr>? stale = null;

        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                if (pair.Value.Refresh(cursor, _globallyVisible))
                {
                    continue;
                }
            }
            catch
            {
                // One closing or hung target must never break the remaining sessions.
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
            if (_sessions.Remove(hwnd, out var session))
            {
                SafeDispose(session);
            }
        }

        RaiseChanged();
    }

    private static PixelPoint? TryCursor()
        => EdgeBarOverlay.TryGetCursor(out var point) ? point : null;

    private static bool IsEligibleTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _geometryTimer.Stop();
        _geometryTimer.Tick -= GeometryTimer_Tick;
        _inputTimer.Stop();
        _inputTimer.Tick -= InputTimer_Tick;

        foreach (var session in _sessions.Values.ToArray())
        {
            SafeDispose(session);
        }
        _sessions.Clear();
    }

}
