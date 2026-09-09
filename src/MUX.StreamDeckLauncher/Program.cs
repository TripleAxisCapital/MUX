using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MUX.StreamDeckLauncher;

internal static class Program
{
    private const string AutoArrangeMessageName = "MUX.Standard.AutoArrange.CursorDisplay.v1";
    private const string BlackBarsMessageName = "MUX.Standard.BlackBars.Toggle.v1";
    private const ulong AutoArrangeAck = 0x4D555841; // MUXA
    private const ulong BlackBarsAck = 0x4D555842;   // MUXB
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

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

            if (TrySignal(command))
            {
                return 0;
            }

            var muxPath = Path.Combine(AppContext.BaseDirectory, "MUX.exe");
            if (!File.Exists(muxPath))
            {
                return 3;
            }

            var argument = command == Command.AutoArrange
                ? "--auto-arrange --background"
                : "--toggle-black-bars --background";

            Process.Start(new ProcessStartInfo
            {
                FileName = muxPath,
                Arguments = argument,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            // Wait for the resident MUX window/message hook to exist, then hand the command over.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(150);
                if (TrySignal(command))
                {
                    return 0;
                }
            }

            return 4;
        }
        catch
        {
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

    private static bool TrySignal(Command command)
    {
        var messageName = command == Command.AutoArrange ? AutoArrangeMessageName : BlackBarsMessageName;
        var expectedAck = command == Command.AutoArrange ? AutoArrangeAck : BlackBarsAck;
        var message = RegisterWindowMessage(messageName);
        if (message == 0)
        {
            return false;
        }

        var delivered = false;
        EnumWindows((hwnd, _) =>
        {
            if (delivered)
            {
                return false;
            }

            try
            {
                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                _ = GetWindowText(hwnd, title, title.Capacity);
                if (!title.ToString().Equals("MUX", StringComparison.Ordinal))
                {
                    return true;
                }

                if (SendMessageTimeout(
                        hwnd,
                        message,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        SmtoBlock | SmtoAbortIfHung,
                        1200,
                        out var result) != IntPtr.Zero &&
                    result.ToUInt64() == expectedAck)
                {
                    delivered = true;
                    return false;
                }
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);

        return delivered;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

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
