using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MUX.App.Services;

/// <summary>
/// Provides a tiny process-to-process command channel for Stream Deck/CLI launchers. A helper
/// invocation never performs Auto Arrange itself when MUX is already resident; it asks the
/// existing UI process to do the work so linked groups, magnetic snapping and persisted state all
/// remain owned by the one long-lived MUX process.
/// </summary>
public sealed class AutoArrangeCommandBridge : IDisposable
{
    private const string RegisteredMessageName = "MUX.Standard.AutoArrange.CursorDisplay.v1";
    private const string LauncherFileName = "MUX-AutoArrange.exe";
    private const long AckValue = 0x4D555841; // "MUXA"
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly uint AutoArrangeMessage = RegisterWindowMessage(RegisteredMessageName);

    private readonly HwndSource _source;
    private readonly Action _callback;
    private bool _disposed;

    private AutoArrangeCommandBridge(HwndSource source, Action callback)
    {
        _source = source;
        _callback = callback;
        _source.AddHook(WndProc);
    }

    public static bool IsAutoArrangeRequest(IEnumerable<string>? args)
        => args?.Any(arg => arg.Equals("--auto-arrange", StringComparison.OrdinalIgnoreCase)) == true;

    public static bool IsLauncherExecutable(string? executablePath)
        => string.Equals(Path.GetFileName(executablePath), LauncherFileName, StringComparison.OrdinalIgnoreCase);

    public static AutoArrangeCommandBridge? Attach(Window window, Action callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var source = hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(hwnd);
            return source is null ? null : new AutoArrangeCommandBridge(source, callback);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends Auto Arrange to another already-running MUX Standard instance. The custom ACK keeps
    /// this compatible with older MUX binaries: an old process that does not understand the command
    /// is not mistaken for a successful receiver.
    /// </summary>
    public static bool TrySignalExistingInstance()
    {
        if (AutoArrangeMessage == 0)
        {
            return false;
        }

        try
        {
            var currentId = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("MUX"))
            {
                using (process)
                {
                    if (process.Id == currentId || process.HasExited)
                    {
                        continue;
                    }

                    foreach (var hwnd in FindMuxMainWindows((uint)process.Id))
                    {
                        if (SendMessageTimeout(
                                hwnd,
                                AutoArrangeMessage,
                                IntPtr.Zero,
                                IntPtr.Zero,
                                SmtoBlock | SmtoAbortIfHung,
                                750,
                                out var result) != IntPtr.Zero &&
                            unchecked((long)result.ToUInt64()) == AckValue)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// The packaged MUX-AutoArrange.exe is a renamed copy of the single-file Standard binary.
    /// If MUX is not running yet, hand off to the canonical MUX.exe and start it in the background.
    /// </summary>
    public static bool TryLaunchStandardSibling()
    {
        try
        {
            var currentPath = Environment.ProcessPath;
            if (!IsLauncherExecutable(currentPath) || string.IsNullOrWhiteSpace(currentPath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(currentPath);
            var standardPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "MUX.exe");
            if (string.IsNullOrWhiteSpace(standardPath) || !File.Exists(standardPath))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = standardPath,
                Arguments = "--auto-arrange --background",
                UseShellExecute = true,
                WorkingDirectory = directory!
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || AutoArrangeMessage == 0 || unchecked((uint)msg) != AutoArrangeMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            _callback();
        }
        catch
        {
            // A Stream Deck command must never be able to crash the resident MUX process.
        }

        return new IntPtr(AckValue);
    }

    private static List<IntPtr> FindMuxMainWindows(uint processId)
    {
        var result = new List<IntPtr>();
        EnumWindowsProc? callback = null;
        callback = (hwnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var ownerProcessId);
                if (ownerProcessId != processId)
                {
                    return true;
                }

                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                _ = GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString().Equals("MUX", StringComparison.Ordinal))
                {
                    result.Add(hwnd);
                }
            }
            catch
            {
            }

            return true;
        };

        _ = EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _source.RemoveHook(WndProc); } catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
