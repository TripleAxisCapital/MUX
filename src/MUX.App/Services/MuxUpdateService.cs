using System.Reflection;
using System.Threading;
using Velopack;
using Velopack.Sources;

namespace MUX.App.Services;

public sealed record MuxUpdateStatus(
    string State,
    string Message,
    int Progress = 0,
    string? Version = null);

/// <summary>
/// Managed MUX update client.
///
/// The installed app never executes source code from GitHub and never compiles itself.
/// GitHub Actions is the trusted release builder; Velopack only downloads and applies
/// a published, versioned MUX package from the repository's stable Releases feed.
/// </summary>
public sealed class MuxUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/TripleAxisCapital/MUX";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _backgroundCts;
    private Task? _backgroundTask;
    private bool _disposed;

    public event EventHandler<MuxUpdateStatus>? StatusChanged;

    public string CurrentVersion
    {
        get
        {
            try
            {
                var manager = CreateManager();
                var managed = manager.CurrentVersion?.ToString();
                if (!string.IsNullOrWhiteSpace(managed))
                {
                    return managed;
                }
            }
            catch
            {
            }

            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
            {
                return "dev";
            }

            return version.Revision > 0
                ? version.ToString(4)
                : version.Build > 0
                    ? version.ToString(3)
                    : version.ToString(2);
        }
    }

    public bool IsManagedInstall
    {
        get
        {
            try
            {
                return CreateManager().IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public void StartAutomaticChecks(TimeSpan? interval = null)
    {
        ThrowIfDisposed();

        if (_backgroundTask is { IsCompleted: false })
        {
            return;
        }

        _backgroundCts?.Dispose();
        _backgroundCts = new CancellationTokenSource();

        var cadence = interval ?? TimeSpan.FromMinutes(5);
        if (cadence < TimeSpan.FromMinutes(1))
        {
            cadence = TimeSpan.FromMinutes(1);
        }

        var ct = _backgroundCts.Token;
        _backgroundTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(cadence, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await CheckAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Emit(
                        "check_failed",
                        "Automatic update check failed · " + Compact(exception.GetBaseException().Message),
                        0,
                        CurrentVersion);
                }
            }
        }, ct);
    }

    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct);

        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                Emit(
                    "setup_required",
                    "Managed updates activate after the one-time MUX setup.",
                    0,
                    CurrentVersion);
                return null;
            }

            Emit("checking", "Checking the managed release channel…", 12, manager.CurrentVersion?.ToString());
            var update = await manager.CheckForUpdatesAsync();
            ct.ThrowIfCancellationRequested();

            if (update is null)
            {
                Emit(
                    "current",
                    "Current · main releases are checked automatically.",
                    100,
                    manager.CurrentVersion?.ToString());
                return null;
            }

            var version = update.TargetFullRelease.Version.ToString();
            Emit("available", $"MUX {version} is ready.", 0, version);
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DownloadAndInstallAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct);

        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                Emit(
                    "setup_required",
                    "Install the managed MUX setup once; do not paste files over the old install.",
                    0,
                    CurrentVersion);
                throw new InvalidOperationException(
                    "This copy of MUX is not installed through the managed update channel. " +
                    "Install the current MUX Setup once to enable in-app updates.");
            }

            Emit("checking", "Checking for the newest MUX release…", 8, manager.CurrentVersion?.ToString());
            var update = await manager.CheckForUpdatesAsync();
            ct.ThrowIfCancellationRequested();

            if (update is null)
            {
                Emit(
                    "current",
                    "Current · no newer managed release is waiting.",
                    100,
                    manager.CurrentVersion?.ToString());
                return false;
            }

            var version = update.TargetFullRelease.Version.ToString();
            Emit("downloading", $"Downloading MUX {version}…", 0, version);

            await manager.DownloadUpdatesAsync(
                update,
                progress => Emit(
                    "downloading",
                    $"Downloading MUX {version} · {progress}%",
                    Math.Clamp(progress, 0, 100),
                    version),
                ct);

            Emit("installing", "Verified · restarting MUX to apply the update.", 100, version);
            await Task.Delay(300, ct);

            manager.ApplyUpdatesAndRestart(update);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static UpdateManager CreateManager()
        => new(new GithubSource(RepositoryUrl, null, prerelease: false));

    private void Emit(string state, string message, int progress = 0, string? version = null)
        => StatusChanged?.Invoke(
            this,
            new MuxUpdateStatus(
                state,
                message,
                Math.Clamp(progress, 0, 100),
                version));

    private static string Compact(string value)
    {
        var compact = string.Join(
            " ",
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return compact.Length <= 180
            ? compact
            : compact[..177] + "...";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MuxUpdateService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var cts = _backgroundCts;
        _backgroundCts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }
}
