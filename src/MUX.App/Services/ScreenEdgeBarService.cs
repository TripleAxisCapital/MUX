using System.Text.Json;
using System.Windows.Threading;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Persistent four-sided black bars attached to physical displays rather than application windows.
/// Each display is configured independently. Thickness is stored as a normalized ratio so layouts
/// survive resolution/DPI changes, and global visibility can be toggled from the tray or Stream Deck
/// without destroying the saved bar positions.
/// </summary>
public sealed partial class ScreenEdgeBarService : IDisposable
{
    private const int StateVersion = 1;
    private static readonly Lazy<ScreenEdgeBarService> SharedInstance = new(() => new ScreenEdgeBarService());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DisplayDiscoveryService _displayDiscovery = new();
    private readonly Dictionary<string, DisplaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScreenBarProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _inputTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _globallyVisible = true;
    private bool _disposed;
    private int _refreshCounter;

    private ScreenEdgeBarService()
    {
        LoadState();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(320)
        };
        _saveTimer.Tick += SaveTimer_Tick;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inputTimer.Tick += InputTimer_Tick;

        SyncDisplays();
        _refreshTimer.Start();
        _inputTimer.Start();
    }

    public static ScreenEdgeBarService Shared => SharedInstance.Value;

    public event EventHandler? Changed;

    public bool AreBarsGloballyVisible => _globallyVisible;
    public int ConfiguredDisplayCount => _profiles.Values.Count(profile => profile.Enabled);

    public bool IsEnabled(string? deviceName)
        => !string.IsNullOrWhiteSpace(deviceName) &&
           _profiles.TryGetValue(deviceName, out var profile) && profile.Enabled;

    public bool SetEnabled(DisplayProfile display, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (_disposed || string.IsNullOrWhiteSpace(display.DeviceName))
        {
            return false;
        }

        if (!_profiles.TryGetValue(display.DeviceName, out var profile))
        {
            profile = CreateDefaultProfile(display);
            _profiles[display.DeviceName] = profile;
        }

        if (profile.Enabled == enabled)
        {
            if (enabled)
            {
                EnsureSession(display, profile);
            }
            return enabled;
        }

        profile.Enabled = enabled;
        if (enabled)
        {
            EnsureSession(display, profile);
        }
        else if (_sessions.Remove(display.DeviceName, out var session))
        {
            SafeDispose(session);
        }

        PersistSoon();
        RaiseChanged();
        return enabled;
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
        RefreshSessions();
        PersistSoon();
        RaiseChanged();
    }

    public void RefreshNow()
    {
        if (_disposed)
        {
            return;
        }

        SyncDisplays();
        RefreshSessions();
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Re-enumerate displays roughly once per second so hot-plug, resolution and topology
        // changes are picked up without making every render tick call EnumDisplayMonitors.
        _refreshCounter++;
        if (_refreshCounter >= 30)
        {
            _refreshCounter = 0;
            SyncDisplays();
        }

        RefreshSessions();
    }

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0 || !EdgeBarOverlay.TryGetCursor(out var cursor))
        {
            return;
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            try { session.UpdateInput(cursor, _globallyVisible); } catch { }
        }
    }

    private void SyncDisplays()
    {
        IReadOnlyList<DisplayProfile> displays;
        try
        {
            displays = _displayDiscovery.GetDisplays();
        }
        catch
        {
            return;
        }

        var detected = displays.ToDictionary(display => display.DeviceName, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _profiles.ToArray())
        {
            if (!pair.Value.Enabled)
            {
                if (_sessions.Remove(pair.Key, out var disabledSession))
                {
                    SafeDispose(disabledSession);
                }
                continue;
            }

            if (detected.TryGetValue(pair.Key, out var display))
            {
                EnsureSession(display, pair.Value);
            }
            else if (_sessions.Remove(pair.Key, out var missingSession))
            {
                // Keep the persisted profile. Reconnecting the monitor restores it automatically.
                SafeDispose(missingSession);
            }
        }
    }

    private void EnsureSession(DisplayProfile display, ScreenBarProfile profile)
    {
        if (_sessions.TryGetValue(display.DeviceName, out var session))
        {
            session.UpdateDisplay(display);
            return;
        }

        _sessions[display.DeviceName] = new DisplaySession(display, profile, OnSessionThicknessChanged);
    }

    private void RefreshSessions()
    {
        var cursor = EdgeBarOverlay.TryGetCursor(out var point) ? point : (PixelPoint?)null;
        foreach (var session in _sessions.Values.ToArray())
        {
            try { session.Refresh(cursor, _globallyVisible); } catch { }
        }
    }

    private void OnSessionThicknessChanged(ScreenBarProfile profile)
    {
        PersistSoon();
    }

    private void PersistSoon()
    {
        if (_disposed)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        PersistState();
    }

    private static ScreenBarProfile CreateDefaultProfile(DisplayProfile display)
    {
        var verticalMinimum = Math.Max(1, Math.Min(display.HeightPx, 6));
        var horizontalMinimum = Math.Max(1, Math.Min(display.WidthPx, 6));
        return new ScreenBarProfile
        {
            DeviceName = display.DeviceName,
            Enabled = false,
            TopRatio = Ratio(verticalMinimum, display.HeightPx),
            RightRatio = Ratio(horizontalMinimum, display.WidthPx),
            BottomRatio = Ratio(verticalMinimum, display.HeightPx),
            LeftRatio = Ratio(horizontalMinimum, display.WidthPx)
        };
    }

}
