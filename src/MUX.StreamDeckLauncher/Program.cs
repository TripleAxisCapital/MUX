using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MUX.StreamDeckLauncher;

internal static class Program
{
    private const string AutoArrangeCommand = "auto-arrange";
    private const string ToggleBlackBarsCommand = "toggle-black-bars";

    private enum Command
    {
        None,
        AutoArrange,
        ToggleBlackBars
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var command = ResolveCommand(args);
            if (command == Command.None)
            {
                return 2;
            }

            var commandText = command == Command.AutoArrange
                ? AutoArrangeCommand
                : ToggleBlackBarsCommand;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MUX",
                "StreamDeckCommands");
            Directory.CreateDirectory(root);

            var id = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var commandPath = Path.Combine(root, id + ".cmd");
            var acknowledgementPath = Path.Combine(root, id + ".ack");
            var tempPath = commandPath + $".tmp-{Environment.ProcessId}";

            File.WriteAllText(tempPath, commandText);
            File.Move(tempPath, commandPath);

            // A running current MUX normally acknowledges in under 100 ms. Giving it a few seconds
            // first avoids launching a duplicate application just because the UI thread was busy.
            if (WaitForAcknowledgement(acknowledgementPath, 3000))
            {
                SafeDelete(acknowledgementPath);
                return 0;
            }

            var muxPath = Path.Combine(AppContext.BaseDirectory, "MUX.exe");
            if (!File.Exists(muxPath))
            {
                WriteFailureLog(commandText, "MUX.exe is not beside the launcher.");
                return 3;
            }

            // If no compatible resident MUX consumed the command, launch the Standard executable
            // from the same package. Its normal startup retires stale older copies and immediately
            // begins draining this same inbox.
            Process.Start(new ProcessStartInfo
            {
                FileName = muxPath,
                Arguments = "--background",
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            if (WaitForAcknowledgement(acknowledgementPath, 12000))
            {
                SafeDelete(acknowledgementPath);
                return 0;
            }

            WriteFailureLog(commandText, "MUX did not acknowledge the command within 15 seconds.");
            return 4;
        }
        catch (Exception exception)
        {
            WriteFailureLog("unknown", $"{exception.GetType().Name}: {exception.Message}");
            return 5;
        }
    }

    private static Command ResolveCommand(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--auto-arrange", StringComparison.OrdinalIgnoreCase))
            {
                return Command.AutoArrange;
            }

            if (arg.Equals("--toggle-black-bars", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--black-bars", StringComparison.OrdinalIgnoreCase))
            {
                return Command.ToggleBlackBars;
            }
        }

        var fileName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (fileName.Contains("BlackBars", StringComparison.OrdinalIgnoreCase))
        {
            return Command.ToggleBlackBars;
        }

        if (fileName.Contains("AutoArrange", StringComparison.OrdinalIgnoreCase))
        {
            return Command.AutoArrange;
        }

        return Command.None;
    }

    private static bool WaitForAcknowledgement(string path, int timeoutMilliseconds)
    {
        var started = Stopwatch.StartNew();
        while (started.ElapsedMilliseconds < timeoutMilliseconds)
        {
            try
            {
                if (File.Exists(path))
                {
                    var value = File.ReadAllText(path).Trim();
                    return value.Equals("OK", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void WriteFailureLog(string command, string message)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MUX");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "streamdeck-launcher.log"),
                $"[{DateTimeOffset.Now:O}] {command}: {message}{Environment.NewLine}");
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
}
