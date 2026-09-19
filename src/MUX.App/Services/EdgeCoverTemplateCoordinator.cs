using System.Text.Json;

namespace MUX.App.Services;

/// <summary>
/// One durable, versioned black-bar template. Geometry and input ownership remain
/// in EnhancedEdgeCoverService; this class does not inspect private WPF fields.
/// </summary>
public sealed class EdgeCoverTemplateCoordinator : IDisposable
{
    private const int TemplateVersion = 1;
    private static readonly Lazy<EdgeCoverTemplateCoordinator> SharedInstance = new(() => new EdgeCoverTemplateCoordinator());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private EdgeCoverTemplate? _template;
    private bool _disposed;

    private EdgeCoverTemplateCoordinator() => _template = LoadTemplate();

    public static EdgeCoverTemplateCoordinator Shared => SharedInstance.Value;
    public bool HasTemplate => _template is not null;

    // Kept as a compatibility entry point for the existing pill UI.
    public void Attach(EnhancedEdgeCoverService service) { }

    public bool SaveTemplate(EnhancedEdgeCoverService service, IntPtr hwnd)
    {
        if (_disposed || !service.TryGetCoverGeometry(hwnd, out var geometry))
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
        if (!template.IsValid() || !PersistTemplate(template))
        {
            return false;
        }

        _template = template;
        return true;
    }

    public bool ApplyTemplate(EnhancedEdgeCoverService service, IntPtr hwnd)
    {
        if (_disposed || _template is null || !service.TryGetCoverGeometry(hwnd, out var geometry))
        {
            return false;
        }

        return service.TrySetCoverThicknesses(hwnd,
            Scale(_template.TopRatio, geometry.Height, geometry.MinimumThickness),
            Scale(_template.RightRatio, geometry.Width, geometry.MinimumThickness),
            Scale(_template.BottomRatio, geometry.Height, geometry.MinimumThickness),
            Scale(_template.LeftRatio, geometry.Width, geometry.MinimumThickness));
    }

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
        _disposed = true;
    }
}
