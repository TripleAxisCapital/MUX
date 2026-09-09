using System.Runtime.CompilerServices;
using System.Threading;

namespace MUX.App.Services;

internal static class BlackBarsCommandBootstrap
{
    private static int _startupRequestPending;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (!BlackBarsCommandBridge.IsToggleRequest(args))
            {
                return;
            }

            if (BlackBarsCommandBridge.TrySignalExistingInstance())
            {
                Environment.Exit(0);
                return;
            }

            Interlocked.Exchange(ref _startupRequestPending, 1);
        }
        catch
        {
            // A command-only launch must never prevent normal MUX startup.
        }
    }

    internal static bool ConsumeStartupRequest()
        => Interlocked.Exchange(ref _startupRequestPending, 0) != 0;
}
