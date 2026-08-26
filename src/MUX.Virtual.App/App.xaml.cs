using MUX.Virtual.App.Models;
using MUX.Virtual.App.Services;

namespace MUX.Virtual.App;

public partial class App : Application
{
    private static readonly string LogRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MUX",
        "Virtual");

    private static readonly string StartupLogPath = Path.Combine(LogRoot, "startup.log");
    private static readonly string StartupErrorLogPath = Path.Combine(LogRoot, "startup-error.log");
    private static readonly string DeviceSmokeLogPath = Path.Combine(LogRoot, "device-smoke.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            LogException("DispatcherUnhandledException", args.Exception);
            TryShowError("MUX Virtual encountered an unexpected error.", args.Exception);
            args.Handled = true;
        };

        try
        {
            WriteStartupLog("Process started.");
            base.OnStartup(e);

            if (e.Args.Any(arg => arg.Equals("--device-smoke", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = await RunDeviceSmokeAsync();
                Shutdown(exitCode);
                return;
            }

            WriteStartupLog("Creating main window.");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
            WriteStartupLog("Main window shown.");
        }
        catch (Exception ex)
        {
            LogException("Fatal startup failure", ex);
            TryShowError("MUX Virtual could not start correctly.", ex);
            Shutdown(-1);
        }
    }

    private static async Task<int> RunDeviceSmokeAsync()
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.WriteAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Starting software-device smoke test.{Environment.NewLine}");

            using var service = new VirtualDeviceService();
            var plan = new VirtualMonitorPlan(
                Guid.Parse("38E109C4-31B0-4FC8-9D8B-5BBE4051DF86"),
                "MUX CI Smoke Monitor",
                new ScreenRect(0, 0, 1280, 720),
                1280,
                720,
                60);

            await service.CreateAsync(new[] { plan });
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Software device created successfully. Instance: {service.DeviceInstanceId}{Environment.NewLine}");
            await Task.Delay(1200);
            return 0;
        }
        catch (Exception ex)
        {
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} FAILED{Environment.NewLine}{ex}{Environment.NewLine}");
            LogException("Device smoke", ex);
            return 31;
        }
    }

    internal static void LogException(string stage, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(
                StartupErrorLogPath,
                $"{DateTimeOffset.Now:O} [{stage}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    internal static void WriteStartupLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void TryShowError(string heading, Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"{heading}{Environment.NewLine}{Environment.NewLine}" +
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Diagnostic details were saved to:{Environment.NewLine}{StartupErrorLogPath}",
                "MUX Virtual Displays",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }
}
