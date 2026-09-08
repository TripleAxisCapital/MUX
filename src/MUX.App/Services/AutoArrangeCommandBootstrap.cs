using System.Runtime.CompilerServices;
using System.Threading;

namespace MUX.App.Services;

/// <summary>
/// Runs before WPF startup so a command-only Stream Deck launch cannot trigger the normal
/// single-instance retirement path and accidentally replace an already-running MUX process.
/// </summary>
internal static class AutoArrangeCommandBootstrap
{
    private static int _startupRequestPending;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var commandRequested = AutoArrangeCommandBridge.IsAutoArrangeRequest(args);
            var launcherRequested = AutoArrangeCommandBridge.IsLauncherExecutable(Environment.ProcessPath);
            if (!commandRequested && !launcherRequested)
            {
                return;
            }

            // Preferred path: ask the resident MUX instance to arrange so its in-memory linked
            // groups and magnetic snapping service remain authoritative.
            if (AutoArrangeCommandBridge.TrySignalExistingInstance())
            {
                Environment.Exit(0);
                return;
            }

            // MUX-AutoArrange.exe is a zero-argument Stream Deck launcher. If MUX is not already
            // running, start the canonical Standard executable in the background and hand it the
            // same command instead of leaving a second differently-named resident process.
            if (launcherRequested && AutoArrangeCommandBridge.TryLaunchStandardSibling())
            {
                Environment.Exit(0);
                return;
            }

            // Direct `MUX.exe --auto-arrange` with no existing instance: let normal WPF startup
            // continue, then consume this flag once display calibration/state loading is complete.
            Interlocked.Exchange(ref _startupRequestPending, 1);
        }
        catch
        {
            // Command bootstrap failure must never prevent normal MUX startup.
        }
    }

    internal static bool ConsumeStartupRequest()
        => Interlocked.Exchange(ref _startupRequestPending, 0) != 0;
}
