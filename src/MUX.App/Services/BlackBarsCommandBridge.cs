using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MUX.App.Services;

/// <summary>
/// Cross-process command bridge for the global black-bar toggle. The dedicated Stream Deck
/// launcher sends a registered Win32 message to the one resident MUX process, which owns the
/// in-memory edge-cover sessions and therefore must perform the toggle itself.
/// </summary>
public sealed class BlackBarsCommandBridge : IDisposable
{
    private const string RegisteredMessageName = "MUX.Standard.BlackBars.Toggle.v1";
    private const long AckValue = 0x4D555842; // MUXB
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly uint ToggleMessage = RegisterWindowMessage(RegisteredMessageName);

    private readonly HwndSource _source;
    private readonly Action _callback;
    private bool _disposed;

    private BlackBarsCommandBridge(HwndSource source, Action callback)
    {
        _source = source;
        _callback = callback;
        _source.AddHook(WndProc);
    }

    public static bool IsToggleRequest(IEnumerable<string>? args)
        => args?.Any(arg =>
            arg.Equals("--toggle-black-bars", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--black-bars", StringComparison.OrdinalIgnoreCase)) == true;

    public static BlackBarsCommandBridge? Attach(Window window, Action callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var source = hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(hwnd);
            return source is null ? null : new BlackBarsCommandBridge(source, callback);
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySignalExistingInstance()
    {
        if (ToggleMessage == 0)
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
                                ToggleMessage,
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || ToggleMessage == 0 || unchecked((uint)msg) != ToggleMessage)
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
            // External command delivery must never crash the resident MUX process.
        }

        return new IntPtr(AckValue);
    }

    private static List<IntPtr> FindMuxMainWindows(uint processId)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
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
        }, IntPtr.Zero);

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
