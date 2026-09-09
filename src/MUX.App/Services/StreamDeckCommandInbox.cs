using System.Text;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Reliable, same-user Stream Deck command transport. Dedicated launcher executables write tiny
/// command files into LocalAppData. MUX claims them atomically on its UI dispatcher, performs the
/// action, and writes an acknowledgement that the launcher waits for. This deliberately avoids
/// global hotkeys, window-title discovery, SendMessage/UIPI, and whether the main window is hidden
/// in the notification area.
/// </summary>
public sealed class StreamDeckCommandInbox : IDisposable
{
    public const string AutoArrangeCommand = "auto-arrange";
    public const string ToggleBlackBarsCommand = "toggle-black-bars";

    private readonly DispatcherTimer _timer;
    private readonly Action _autoArrange;
    private readonly Action _toggleBlackBars;
    private readonly string _commandRoot;
    private readonly string _logPath;
    private bool _disposed;

    public StreamDeckCommandInbox(
        Dispatcher dispatcher,
        Action autoArrange,
        Action toggleBlackBars)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(autoArrange);
        ArgumentNullException.ThrowIfNull(toggleBlackBars);

        _autoArrange = autoArrange;
        _toggleBlackBars = toggleBlackBars;
        _commandRoot = GetCommandRoot();
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "streamdeck-command.log");

        _timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        _timer.Tick += Timer_Tick;
    }

    public static string GetCommandRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MUX",
            "StreamDeckCommands");

    public void Start()
    {
        if (_disposed || _timer.IsEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_commandRoot);
            CleanupStaleFiles();
        }
        catch
        {
            // The timer will retry directory access on its first tick.
        }

        _timer.Start();
        DrainInbox();
    }

    private void Timer_Tick(object? sender, EventArgs e)
        => DrainInbox();

    private void DrainInbox()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_commandRoot);
        }
        catch
        {
            return;
        }

        string[] pending;
        try
        {
            pending = Directory.GetFiles(_commandRoot, "*.cmd")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray();
        }
        catch
        {
            return;
        }

        foreach (var path in pending)
        {
            ProcessOne(path);
        }
    }

    private void ProcessOne(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(id))
        {
            SafeDelete(path);
            return;
        }

        var claimedPath = path + $".processing-{Environment.ProcessId}";
        try
        {
            // Atomic claim: if two MUX processes briefly overlap, only one can execute the command.
            File.Move(path, claimedPath);
        }
        catch
        {
            return;
        }

        var acknowledged = false;
        try
        {
            var command = File.ReadAllText(claimedPath, Encoding.UTF8).Trim();
            switch (command.ToLowerInvariant())
            {
                case AutoArrangeCommand:
                    _autoArrange();
                    acknowledged = true;
                    break;

                case ToggleBlackBarsCommand:
                    _toggleBlackBars();
                    acknowledged = true;
                    break;

                default:
                    AppendLog($"ignored {id} unknown command '{command}'");
                    break;
            }

            if (acknowledged)
            {
                WriteAcknowledgement(id);
                AppendLog($"executed {id} {command}");
            }
        }
        catch (Exception exception)
        {
            AppendLog($"failed {id} {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            SafeDelete(claimedPath);
        }
    }

    private void WriteAcknowledgement(string id)
    {
        var finalPath = Path.Combine(_commandRoot, id + ".ack");
        var tempPath = finalPath + $".tmp-{Environment.ProcessId}";

        try
        {
            File.WriteAllText(tempPath, "OK", Encoding.ASCII);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            SafeDelete(tempPath);
        }
    }

    private void CleanupStaleFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (var path in Directory.GetFiles(_commandRoot))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        SafeDelete(path);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(
                _logPath,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
