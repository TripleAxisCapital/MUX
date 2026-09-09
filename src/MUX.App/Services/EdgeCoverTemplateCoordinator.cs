using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Durable default templates plus the native hit-test layer for EnhancedEdgeCoverService.
/// Black surfaces remain visual-only, while a narrow band around each adjustable inner edge stays
/// genuinely interactive so users can always click-drag the bars without blocking the target app.
/// </summary>
public sealed class EdgeCoverTemplateCoordinator : IDisposable
{
    private const int TemplateVersion = 1;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcDestroy = 0x0082;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int TopInteractiveBandDip = 12;
    private const int OtherInteractiveBandDip = 28;

    private static readonly Lazy<EdgeCoverTemplateCoordinator> SharedInstance = new(() => new EdgeCoverTemplateCoordinator());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DispatcherTimer _inputTimer;
    private readonly Dictionary<IntPtr, OverlayBinding> _overlayBindings = new();
    private readonly SubclassProc _subclassProc;
    private EdgeCoverTemplate? _template;
    private EnhancedEdgeCoverService? _service;
    private FieldInfo? _sessionsField;
    private bool _disposed;

    private EdgeCoverTemplateCoordinator()
    {
        _template = LoadTemplate();
        _subclassProc = OverlaySubclassProc;
        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(45)
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
        EnsureOverlaySubclasses();
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
        if (_disposed)
        {
            return;
        }

        try
        {
            EnsureOverlaySubclasses();
            PruneDeadOverlayBindings();
        }
        catch
        {
            // Input polish must never be able to affect cover rendering or the host application.
        }
    }

    private void EnsureOverlaySubclasses()
    {
        if (GetSessions() is not IDictionary sessions)
        {
            return;
        }

        var enumerator = sessions.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var session = enumerator.Value;
            if (session is null)
            {
                continue;
            }

            EnsureOverlaySubclass(session, "_top", OverlaySide.Top);
            EnsureOverlaySubclass(session, "_right", OverlaySide.Right);
            EnsureOverlaySubclass(session, "_bottom", OverlaySide.Bottom);
            EnsureOverlaySubclass(session, "_left", OverlaySide.Left);
        }
    }

    private void EnsureOverlaySubclass(object session, string fieldName, OverlaySide side)
    {
        var overlay = session.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) as Window;
        if (overlay is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(overlay).Handle;
        if (hwnd == IntPtr.Zero || _overlayBindings.ContainsKey(hwnd))
        {
            return;
        }

        var id = unchecked((UIntPtr)(ulong)hwnd.ToInt64());
        if (!SetWindowSubclass(hwnd, _subclassProc, id, UIntPtr.Zero))
        {
            return;
        }

        _overlayBindings[hwnd] = new OverlayBinding(session, side, id);
    }

    private IntPtr OverlaySubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == WmNcDestroy)
        {
            _overlayBindings.Remove(hwnd);
            _ = RemoveWindowSubclass(hwnd, _subclassProc, subclassId);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        if (message == WmNcHitTest && _overlayBindings.TryGetValue(hwnd, out var binding))
        {
            if (!GetCursorPos(out var cursor) || !TryReadGeometry(binding.Session, out var geometry))
            {
                return new IntPtr(HtTransparent);
            }

            return new IntPtr(IsInteractiveEdgePoint(binding.Side, cursor, geometry)
                ? HtClient
                : HtTransparent);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static bool IsInteractiveEdgePoint(OverlaySide side, NativePoint point, SessionGeometry geometry)
    {
        var topBand = ScaleForDpi(TopInteractiveBandDip, geometry.Dpi);
        var otherBand = ScaleForDpi(OtherInteractiveBandDip, geometry.Dpi);

        return side switch
        {
            OverlaySide.Top =>
                point.X >= geometry.Left && point.X < geometry.Right &&
                Math.Abs(point.Y - (geometry.Top + geometry.TopThickness)) <= topBand,
            OverlaySide.Bottom =>
                point.X >= geometry.Left && point.X < geometry.Right &&
                Math.Abs(point.Y - (geometry.Bottom - geometry.BottomThickness)) <= otherBand,
            OverlaySide.Left =>
                point.Y >= geometry.Top && point.Y < geometry.Bottom &&
                Math.Abs(point.X - (geometry.Left + geometry.LeftThickness)) <= otherBand,
            OverlaySide.Right =>
                point.Y >= geometry.Top && point.Y < geometry.Bottom &&
                Math.Abs(point.X - (geometry.Right - geometry.RightThickness)) <= otherBand,
            _ => false
        };
    }

    private void PruneDeadOverlayBindings()
    {
        foreach (var pair in _overlayBindings.ToArray())
        {
            if (IsWindow(pair.Key))
            {
                continue;
            }

            _overlayBindings.Remove(pair.Key);
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

        foreach (var pair in _overlayBindings.ToArray())
        {
            try
            {
                if (IsWindow(pair.Key))
                {
                    _ = RemoveWindowSubclass(pair.Key, _subclassProc, pair.Value.SubclassId);
                }
            }
            catch
            {
            }
        }

        _overlayBindings.Clear();
        _service = null;
    }

    private enum OverlaySide
    {
        Top,
        Right,
        Bottom,
        Left
    }

    private sealed record OverlayBinding(object Session, OverlaySide Side, UIntPtr SubclassId);

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

    private delegate IntPtr SubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
