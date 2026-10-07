using System.Text.Json;

namespace MUX.App.Services;

public sealed partial class ScreenEdgeBarService
{
    private void LoadState()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<ScreenBarState>(File.ReadAllText(path));
            if (state is null || state.Version != StateVersion)
            {
                return;
            }

            _globallyVisible = state.GloballyVisible;
            foreach (var profile in state.Displays.Where(profile => profile.IsValid()))
            {
                _profiles[profile.DeviceName] = profile;
            }
        }
        catch
        {
            // Corrupt settings must not prevent MUX from starting.
        }
    }

    private void PersistState()
    {
        try
        {
            var path = GetStatePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new ScreenBarState
            {
                Version = StateVersion,
                GloballyVisible = _globallyVisible,
                Displays = _profiles.Values
                    .OrderBy(profile => profile.DeviceName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var temporary = path + $".tmp-{Environment.ProcessId}";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, path, true);
        }
        catch
        {
        }
    }

    private static string GetStatePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "screen-edge-bars.json");

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
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _inputTimer.Stop();
        _inputTimer.Tick -= InputTimer_Tick;
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        PersistState();

        foreach (var session in _sessions.Values.ToArray())
        {
            SafeDispose(session);
        }
        _sessions.Clear();
    }

}
