using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Durable default templates plus an input-polish layer for EnhancedEdgeCoverService. The black
/// surfaces stay click-through, but the adjustable inner edge is armed before a click and remains
/// interactive for the complete drag gesture. This preserves the caption pill and target-window
/// input without sacrificing direct black-bar dragging.
/// </summary>
public sealed class EdgeCoverTemplateCoordinator : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const int TopInteractiveBandDip = 20;
    private const int OtherInteractiveBandDip = 34;
    private const int CaptionReserveDip = 190;
    private const int MinimumTopDragWidthDip = 96;
    private const int TemplateVersion = 1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private static readonly Lazy<EdgeCoverTemplateCoordinator> SharedInstance = new(() => new EdgeCoverTemplateCoordinator());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DispatcherTimer _inputTimer;
    private readonly Dictionary<IntPtr, bool> _transparentState = new();
    private EdgeCoverTemplate? _template;
    private EnhancedEdgeCoverService? _service;
    private FieldInfo? _sessionsField;
    private bool _disposed;

    private EdgeCoverTemplateCoordinator()
    {
        _template = LoadTemplate();
        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            // Arm the native overlay well before a normal mouse-down arrives. The old 32 ms pass
            // could leave WS_EX_TRANSPARENT cached for the click that was supposed to begin a drag.
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inputTimer.Tick += InputTimer_Tick;
        _inputTimer.Start();
    }

    public static EdgeCoverTemplateCoordinator Shared => SharedInstance.Value;
    public bool HasTemplate => _template is not null;

    public void Attach(EnhancedEdgeCoverService service)
    {
        if (_disposed)
        {
            return;
        }

        _service = service;
        _sessionsField ??= typeof(EnhancedEdgeCoverService).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public bool SaveTemplate(EnhancedEdgeCoverService service, IntPtr hwnd)
    {
        Attach(service);
        if (!TryGetSession(hwnd, out var session) || !TryReadGeometry(session, out var geometry))
        {
            return false;
        }

        var template = new EdgeCoverTemplate
        {
            Version = TemplateVersion,
            TopRatio = Ratio(geometry.TopThickness, geometry.Height),
            RightRatio = Ratio(geometry.RightThickness, geometry.Width),
            BottomRatio = Ratio(geometry.BottomThickness, geometry.Height),
            LeftRatio = Ratio(geometry.LeftThickness, geometry.Width)
        };
        if (!template.IsValid())
        {
            return false;
        }

        _template = template;
        return PersistTemplate(template);
    }

    public bool ApplyTemplate(EnhancedEdgeCoverService service, IntPtr hwnd)
    {
        Attach(service);
        if (_template is null || !TryGetSession(hwnd, out var session) || !TryReadGeometry(session, out var geometry))
        {
            return false;
        }

        try
        {
            var type = session.GetType();
            SetInt(type, session, "_topThickness", Scale(_template.TopRatio, geometry.Height, geometry.MinimumThickness));
            SetInt(type, session, "_rightThickness", Scale(_template.RightRatio, geometry.Width, geometry.MinimumThickness));
            SetInt(type, session, "_bottomThickness", Scale(_template.BottomRatio, geometry.Height, geometry.MinimumThickness));
            SetInt(type, session, "_leftThickness", Scale(_template.LeftRatio, geometry.Width, geometry.MinimumThickness));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !GetCursorPos(out var cursor) || GetSessions() is not IDictionary sessions)
        {
            return;
        }

        try
        {
            var enumerator = sessions.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var session = enumerator.Value;
                if (session is null || !TryReadGeometry(session, out var geometry))
                {
                    continue;
                }

                UpdateOverlayInput(session, "_top", EdgeSide.Top, cursor, geometry);
                UpdateOverlayInput(session, "_right", EdgeSide.Right, cursor, geometry);
                UpdateOverlayInput(session, "_bottom", EdgeSide.Bottom, cursor, geometry);
                UpdateOverlayInput(session, "_left", EdgeSide.Left, cursor, geometry);
            }

            PruneDeadOverlayState();
        }
        catch
        {
            // Input polish is isolated from rendering and may never destabilize the host app.
        }
    }

    private void UpdateOverlayInput(
        object session,
        string fieldName,
        EdgeSide side,
        NativePoint cursor,
        SessionGeometry geometry)
    {
        var overlay = session.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) as Window;
        if (overlay is null || !overlay.IsVisible)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(overlay).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Never turn an overlay click-through in the middle of a captured drag. That transition was
        // the core regression: the initial click could land, then the first mouse move lost the
        // overlay and the bar appeared frozen.
        var dragging = ReadBool(overlay.GetType(), overlay, "_dragging");
        var interactive = dragging || IsNearAdjustableBoundary(side, cursor, geometry);
        SetTransparent(hwnd, !interactive);
    }

    private static bool IsNearAdjustableBoundary(EdgeSide side, NativePoint cursor, SessionGeometry geometry)
    {
        var topBand = ScaleForDpi(TopInteractiveBandDip, geometry.Dpi);
        var otherBand = ScaleForDpi(OtherInteractiveBandDip, geometry.Dpi);

        if (side == EdgeSide.Top)
        {
            var captionReserve = Math.Min(
                ScaleForDpi(CaptionReserveDip, geometry.Dpi),
                Math.Max(0, geometry.Width - ScaleForDpi(MinimumTopDragWidthDip, geometry.Dpi)));
            var captionStart = geometry.Right - captionReserve;

            // Keep the caption-button cluster completely click-through so hovering minimize,
            // maximize, or close can still reveal the MUX pill even with a very thin top bar.
            if (captionReserve > 0 && cursor.X >= captionStart)
            {
                return false;
            }

            return cursor.X >= geometry.Left && cursor.X < geometry.Right &&
                   Math.Abs(cursor.Y - (geometry.Top + geometry.TopThickness)) <= topBand;
        }

        return side switch
        {
            EdgeSide.Bottom =>
                cursor.X >= geometry.Left && cursor.X < geometry.Right &&
                Math.Abs(cursor.Y - (geometry.Bottom - geometry.BottomThickness)) <= otherBand,
            EdgeSide.Left =>
                cursor.Y >= geometry.Top && cursor.Y < geometry.Bottom &&
                Math.Abs(cursor.X - (geometry.Left + geometry.LeftThickness)) <= otherBand,
            EdgeSide.Right =>
                cursor.Y >= geometry.Top && cursor.Y < geometry.Bottom &&
                Math.Abs(cursor.X - (geometry.Right - geometry.RightThickness)) <= otherBand,
            _ => false
        };
    }

    private void SetTransparent(IntPtr hwnd, bool transparent)
    {
        if (_transparentState.TryGetValue(hwnd, out var previous) && previous == transparent)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var next = transparent ? style | WsExTransparent : style & ~WsExTransparent;
        if (next != style)
        {
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next));

            // SetWindowLongPtr can leave extended-style state cached. Force a non-moving,
            // non-sizing frame refresh so the hit-test behavior changes before the user's click.
            _ = SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);
        }

        _transparentState[hwnd] = transparent;
    }

    private void PruneDeadOverlayState()
    {
        foreach (var hwnd in _transparentState.Keys.ToArray())
        {
            if (!IsWindow(hwnd))
            {
                _transparentState.Remove(hwnd);
            }
        }
    }

    private object? GetSessions()
        => _service is null ? null : _sessionsField?.GetValue(_service);

    private bool TryGetSession(IntPtr hwnd, out object session)
    {
        session = null!;
        if (GetSessions() is not IDictionary sessions || !sessions.Contains(hwnd))
        {
            return false;
        }

        session = sessions[hwnd]!;
        return session is not null;
    }

    private static bool TryReadGeometry(object session, out SessionGeometry geometry)
    {
        geometry = default;
        try
        {
            var type = session.GetType();
            var rect = type.GetField("_targetRect", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session);
            if (rect is null)
            {
                return false;
            }

            var rectType = rect.GetType();
            var left = ReadRect(rectType, rect, "Left");
            var top = ReadRect(rectType, rect, "Top");
            var right = ReadRect(rectType, rect, "Right");
            var bottom = ReadRect(rectType, rect, "Bottom");
            var width = right - left;
            var height = bottom - top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            geometry = new SessionGeometry(
                left,
                top,
                right,
                bottom,
                width,
                height,
                ReadInt(type, session, "_minimumThickness"),
                ReadInt(type, session, "_topThickness"),
                ReadInt(type, session, "_rightThickness"),
                ReadInt(type, session, "_bottomThickness"),
                ReadInt(type, session, "_leftThickness"),
                ReadUInt(type, session, "_dpi"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadRect(Type type, object instance, string name)
        => (int)(type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) ?? 0);

    private static int ReadInt(Type type, object instance, string name)
        => Convert.ToInt32(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? 0);

    private static uint ReadUInt(Type type, object instance, string name)
        => Convert.ToUInt32(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? 96u);

    private static bool ReadBool(Type type, object instance, string name)
        => Convert.ToBoolean(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? false);

    private static void SetInt(Type type, object instance, string name, int value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(instance, value);

    private static double Ratio(int value, int dimension)
        => Math.Clamp((double)Math.Max(0, value) / Math.Max(1, dimension), 0.0, 1.0);

    private static int Scale(double ratio, int dimension, int minimum)
        => Math.Clamp((int)Math.Round(dimension * ratio), Math.Max(1, minimum), Math.Max(Math.Max(1, minimum), dimension));

    private static int ScaleForDpi(int dip, uint dpi)
        => Math.Max(1, (int)Math.Round(dip * Math.Max(96u, dpi) / 96.0));

    private sealed class EdgeCoverTemplate
    {
        public int Version { get; set; } = TemplateVersion;
        public double TopRatio { get; set; }
        public double RightRatio { get; set; }
        public double BottomRatio { get; set; }
        public double LeftRatio { get; set; }

        public bool IsValid()
            => Version == TemplateVersion && Valid(TopRatio) && Valid(RightRatio) && Valid(BottomRatio) && Valid(LeftRatio);

        private static bool Valid(double value)
            => double.IsFinite(value) && value >= 0 && value <= 1;
    }

    private static EdgeCoverTemplate? LoadTemplate()
    {
        try
        {
            var path = GetTemplatePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var template = JsonSerializer.Deserialize<EdgeCoverTemplate>(File.ReadAllText(path));
            return template?.IsValid() == true ? template : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool PersistTemplate(EdgeCoverTemplate template)
    {
        try
        {
            var path = GetTemplatePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(template, JsonOptions));
            File.Move(temporary, path, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTemplatePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MUX", "edge-cover-template.json");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputTimer.Stop();
        _inputTimer.Tick -= InputTimer_Tick;

        // Restore normal overlay styles before detaching. This shared coordinator normally lives
        // for the process lifetime, but cleanup must never strand helper windows in transparent mode.
        foreach (var hwnd in _transparentState.Keys.ToArray())
        {
            try
            {
                if (IsWindow(hwnd))
                {
                    SetTransparent(hwnd, false);
                }
            }
            catch
            {
            }
        }

        _transparentState.Clear();
        _service = null;
    }

    private enum EdgeSide
    {
        Top,
        Right,
        Bottom,
        Left
    }

    private readonly record struct SessionGeometry(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int Width,
        int Height,
        int MinimumThickness,
        int TopThickness,
        int RightThickness,
        int BottomThickness,
        int LeftThickness,
        uint Dpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
