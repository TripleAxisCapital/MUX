using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MUX.StreamDeckLauncher;

internal static class Program
{
    private const string AutoArrangeCommand = "auto-arrange";
    private const string ToggleBlackBarsCommand = "toggle-black-bars";
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;

    private enum Command
    {
        None,
        AutoArrange,
        ToggleBlackBars
    }

    private readonly record struct Acknowledgement(bool Received, bool Success, string Message);

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

            // A running current MUX normally acknowledges in under 100 ms. The acknowledgement now
            // represents the result of the real operation, not merely that Auto Arrange was queued.
            var acknowledgement = WaitForAcknowledgement(acknowledgementPath, 3000);
            if (acknowledgement.Received)
            {
                SafeDelete(acknowledgementPath);
                return Complete(command, acknowledgement);
            }

            var muxPath = Path.Combine(AppContext.BaseDirectory, "MUX.exe");
            if (!File.Exists(muxPath))
            {
                WriteFailureLog(commandText, "MUX.exe is not beside the launcher.");
                ShowFailure(command, "MUX.exe is not beside this Stream Deck launcher.");
                return 3;
            }

            // If no compatible resident MUX consumed the command, launch Standard from the same
            // package. Its command inbox will pick up the already-written request after startup.
            Process.Start(new ProcessStartInfo
            {
                FileName = muxPath,
                Arguments = "--background",
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            acknowledgement = WaitForAcknowledgement(acknowledgementPath, 12000);
            if (acknowledgement.Received)
            {
                SafeDelete(acknowledgementPath);
                return Complete(command, acknowledgement);
            }

            WriteFailureLog(commandText, "MUX did not acknowledge the command within 15 seconds.");
            ShowFailure(command, "MUX did not acknowledge the command. Make sure the new MUX.exe from this same folder is running.");
            return 4;
        }
        catch (Exception exception)
        {
            WriteFailureLog("unknown", $"{exception.GetType().Name}: {exception.Message}");
            ShowFailure(Command.None, $"MUX Stream Deck launcher failed: {exception.Message}");
            return 5;
        }
    }

    private static int Complete(Command command, Acknowledgement acknowledgement)
    {
        if (acknowledgement.Success)
        {
            return 0;
        }

        var commandText = command == Command.AutoArrange ? AutoArrangeCommand : ToggleBlackBarsCommand;
        WriteFailureLog(commandText, acknowledgement.Message);
        ShowFailure(command, acknowledgement.Message);
        return 6;
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

    private static Acknowledgement WaitForAcknowledgement(string path, int timeoutMilliseconds)
    {
        var started = Stopwatch.StartNew();
        while (started.ElapsedMilliseconds < timeoutMilliseconds)
        {
            try
            {
                if (File.Exists(path))
                {
                    var value = File.ReadAllText(path).Trim();
                    if (value.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        return new Acknowledgement(true, true, ExtractMessage(value, "OK"));
                    }

                    if (value.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return new Acknowledgement(true, false, ExtractMessage(value, "ERROR"));
                    }

                    // Backward compatibility with the first inbox build, which wrote plain "OK".
                    if (value.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        return new Acknowledgement(true, true, string.Empty);
                    }
                }
            }
            catch
            {
            }

            Thread.Sleep(50);
        }

        return new Acknowledgement(false, false, string.Empty);
    }

    private static string ExtractMessage(string value, string prefix)
    {
        if (value.Length <= prefix.Length)
        {
            return prefix.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                ? "MUX could not complete the command."
                : string.Empty;
        }

        var message = value[prefix.Length..].TrimStart('|', ':', ' ');
        return string.IsNullOrWhiteSpace(message)
            ? "MUX could not complete the command."
            : message;
    }

    private static void ShowFailure(Command command, string message)
    {
        try
        {
            // GitHub Actions has no person to dismiss a dialog. On a real desktop this makes a
            // failed Stream Deck press self-explanatory instead of appearing to do nothing.
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var title = command == Command.AutoArrange
                ? "MUX Auto Arrange"
                : command == Command.ToggleBlackBars
                    ? "MUX Black Bars"
                    : "MUX Stream Deck";
            _ = MessageBox(IntPtr.Zero, message, title, MbOk | MbIconInformation);
        }
        catch
        {
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
