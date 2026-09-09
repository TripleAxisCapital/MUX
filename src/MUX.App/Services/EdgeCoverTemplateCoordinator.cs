using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Durable default templates plus an input-polish layer for EnhancedEdgeCoverService. The renderer
/// intentionally stays unchanged; this adapter keeps its compatibility surface stable while making
/// the black surface click-through everywhere except the adjustable inner edge.
/// </summary>
public sealed class EdgeCoverTemplateCoordinator : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const int InteractiveBandPx = 30;
    private const int TemplateVersion = 1;

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
            Interval = TimeSpan.FromMilliseconds(32)
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
            // The renderer's 25 ms refresh pass clamps and redraws these values immediately.
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

                UpdateOverlayInput(session, "_top", Math.Abs(cursor.Y - (geometry.Top + geometry.TopThickness)), cursor.X >= geometry.Left && cursor.X <= geometry.Right);
                UpdateOverlayInput(session, "_bottom", Math.Abs(cursor.Y - (geometry.Bottom - geometry.BottomThickness)), cursor.X >= geometry.Left && cursor.X <= geometry.Right);
                UpdateOverlayInput(session, "_left", Math.Abs(cursor.X - (geometry.Left + geometry.LeftThickness)), cursor.Y >= geometry.Top && cursor.Y <= geometry.Bottom);
                UpdateOverlayInput(session, "_right", Math.Abs(cursor.X - (geometry.Right - geometry.RightThickness)), cursor.Y >= geometry.Top && cursor.Y <= geometry.Bottom);
            }
        }
        catch
        {
            // This is input polish only. Rendering must never depend on it.
        }
    }

    private void UpdateOverlayInput(object session, string fieldName, int boundaryDistance, bool alongEdge)
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

        // Only the resize boundary captures the pointer. The rest behaves like visual glass, which
        // prevents a top black bar from stealing the caption hover used to reveal the MUX pill.
        SetTransparent(hwnd, !(alongEdge && boundaryDistance <= InteractiveBandPx));
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
        }
        _transparentState[hwnd] = transparent;
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
                left, top, right, bottom, width, height,
                ReadInt(type, session, "_minimumThickness"),
                ReadInt(type, session, "_topThickness"),
                ReadInt(type, session, "_rightThickness"),
                ReadInt(type, session, "_bottomThickness"),
                ReadInt(type, session, "_leftThickness"));
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
        => (int)(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? 0);

    private static void SetInt(Type type, object instance, string name, int value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(instance, value);

    private static double Ratio(int value, int dimension)
        => Math.Clamp((double)Math.Max(0, value) / Math.Max(1, dimension), 0.0, 1.0);

    private static int Scale(double ratio, int dimension, int minimum)
        => Math.Clamp((int)Math.Round(dimension * ratio), Math.Max(1, minimum), Math.Max(Math.Max(1, minimum), dimension));

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
        _transparentState.Clear();
        _service = null;
    }

    private readonly record struct SessionGeometry(
        int Left, int Top, int Right, int Bottom, int Width, int Height, int MinimumThickness,
        int TopThickness, int RightThickness, int BottomThickness, int LeftThickness);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

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
