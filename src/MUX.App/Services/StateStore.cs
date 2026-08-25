using System.Text.Json;
using MUX.Core.Models;

namespace MUX.App.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statePath;

    public StateStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MUX");
        Directory.CreateDirectory(root);
        _statePath = Path.Combine(root, "state.json");
    }

    public async Task<MuxState> LoadAsync()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return new MuxState();
            }

            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<MuxState>(stream, JsonOptions) ?? new MuxState();
        }
        catch
        {
            var backup = _statePath + ".corrupt-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_statePath, backup, overwrite: true); } catch { }
            return new MuxState();
        }
    }

    public async Task SaveAsync(MuxState state)
    {
        var temp = _statePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
        }

        File.Move(temp, _statePath, overwrite: true);
    }

    public static T DeepClone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }
}
