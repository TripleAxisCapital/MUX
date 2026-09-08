using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MUX.App.Services;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private sealed class MagneticSnapPreferences
    {
        public bool Enabled { get; set; } = true;
    }

    private MagneticSnapService? _magneticSnapService;
    private bool _magneticSnappingEnabled = true;

    private void MagnetWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_magneticSnapService is null)
        {
            _magneticSnappingEnabled = LoadMagneticSnappingEnabled();
            _magneticSnapService = new MagneticSnapService(_magneticSnappingEnabled);
        }

        UpdateMagnetVisual();
        InitializeEdgeCoverControls();
    }

    private void MagnetWindow_Closed(object? sender, EventArgs e)
    {
        DisposeEdgeCoverControls();

        _magneticSnapService?.Dispose();
        _magneticSnapService = null;
    }

    private void MagnetButton_Click(object sender, RoutedEventArgs e)
    {
        _magneticSnappingEnabled = !_magneticSnappingEnabled;
        if (_magneticSnapService is not null)
        {
            _magneticSnapService.Enabled = _magneticSnappingEnabled;
        }

        SaveMagneticSnappingEnabled(_magneticSnappingEnabled);
        UpdateMagnetVisual();
    }

    private void UpdateMagnetVisual()
    {
        if (MagnetButton is null || MagnetGlyph is null)
        {
            return;
        }

        MagnetButton.Background = new SolidColorBrush(
            _magneticSnappingEnabled
                ? Color.FromRgb(62, 62, 69)
                : Color.FromRgb(31, 31, 35));
        MagnetButton.Opacity = _magneticSnappingEnabled ? 1.0 : 0.62;
        MagnetGlyph.Fill = new SolidColorBrush(
            _magneticSnappingEnabled
                ? Color.FromRgb(245, 245, 247)
                : Color.FromRgb(156, 156, 164));
        MagnetButton.ToolTip = _magneticSnappingEnabled
            ? "Magnetic window snapping · On"
            : "Magnetic window snapping · Off";
    }

    private static bool LoadMagneticSnappingEnabled()
    {
        try
        {
            var path = GetMagneticSnappingPreferencesPath();
            if (!File.Exists(path))
            {
                return true;
            }

            var preferences = JsonSerializer.Deserialize<MagneticSnapPreferences>(File.ReadAllText(path));
            return preferences?.Enabled ?? true;
        }
        catch
        {
            return true;
        }
    }

    private static void SaveMagneticSnappingEnabled(bool enabled)
    {
        try
        {
            var path = GetMagneticSnappingPreferencesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new MagneticSnapPreferences { Enabled = enabled },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // This is convenience state. A settings write must never interrupt the resize pill.
        }
    }

    private static string GetMagneticSnappingPreferencesPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "magnetic-snapping.json");
    }
}
