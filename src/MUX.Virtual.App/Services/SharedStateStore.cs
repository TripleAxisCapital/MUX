namespace MUX.Virtual.App.Services;

public sealed class SharedStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MUX",
        "state.json");

    public async Task<MuxState> LoadAsync()
    {
        if (!File.Exists(_statePath))
        {
            return new MuxState();
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<MuxState>(stream, JsonOptions)
                ?? new MuxState();
        }
        catch
        {
            return new MuxState();
        }
    }
}
