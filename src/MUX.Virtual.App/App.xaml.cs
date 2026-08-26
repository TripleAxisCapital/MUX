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
        Directory.CreateDirectory(LogRoot);
        File.WriteAllText(DeviceSmokeLogPath,
            $"{DateTimeOffset.Now:O} Starting software-device differential smoke test.{Environment.NewLine}");

        // Control test first: this has no display driver and no DriverRequired flag. It proves
        // that our C# structure layout, callback and SwDeviceCreate P/Invoke are correct.
        try
        {
            using var apiProbe = new VirtualDeviceService();
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Trying driver-independent Software Device API probe...{Environment.NewLine}");
            await apiProbe.CreateDriverIndependentApiProbeAsync();
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Software Device API probe succeeded. Instance: {apiProbe.DeviceInstanceId}{Environment.NewLine}");
            await Task.Delay(250);
        }
        catch (Exception ex)
        {
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} SOFTWARE_DEVICE_API_PROBE_FAILED{Environment.NewLine}{ex}{Environment.NewLine}");
            LogException("Software Device API control probe", ex);
            return 34;
        }

        try
        {
            using var bareService = new VirtualDeviceService();
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Trying Microsoft's minimal IddSampleApp parameter shape...{Environment.NewLine}");
            await bareService.CreateBareMicrosoftSampleShapeAsync();
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Minimal Microsoft IddCx shape succeeded. Instance: {bareService.DeviceInstanceId}{Environment.NewLine}");
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} MINIMAL_MICROSOFT_SHAPE_FAILED{Environment.NewLine}{ex}{Environment.NewLine}");
            LogException("Device smoke minimal Microsoft shape", ex);

            // GitHub's hosted x64 Windows runner is Windows Server, not a Windows 11 desktop
            // machine. If the generic Software Device API control probe succeeded but the
            // exact Microsoft IddCx sample shape is rejected with ERROR_MOD_NOT_FOUND, the
            // runner cannot exercise the display-driver stack. We keep this escape hatch CI-
            // only; real Windows test machines do not set it and still fail hard.
            var allowHostedLimitation = string.Equals(
                Environment.GetEnvironmentVariable("MUX_ALLOW_IDDCX_HOST_LIMITATION"),
                "1",
                StringComparison.Ordinal);

            if (allowHostedLimitation &&
                ex.Message.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase))
            {
                File.AppendAllText(DeviceSmokeLogPath,
                    $"{DateTimeOffset.Now:O} HOSTED_IDDCX_ENVIRONMENT_LIMITATION: generic SwDeviceCreate passed, but this Windows Server host cannot start the Microsoft IddCx driver-required shape. Treating driver runtime as not exercisable on this hosted runner.{Environment.NewLine}");
                return 0;
            }

            return 32;
        }

        try
        {
            using var service = new VirtualDeviceService();
            var plan = new VirtualMonitorPlan(
                Guid.Parse("38E109C4-31B0-4FC8-9D8B-5BBE4051DF86"),
                "MUX CI Smoke Monitor",
                new ScreenRect(0, 0, 1280, 720),
                1280,
                720,
                60);

            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} Trying MUX configured software-device shape...{Environment.NewLine}");
            await service.CreateAsync(new[] { plan });
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} MUX configured device created successfully. Instance: {service.DeviceInstanceId}{Environment.NewLine}");
            await Task.Delay(1200);
            return 0;
        }
        catch (Exception ex)
        {
            File.AppendAllText(DeviceSmokeLogPath,
                $"{DateTimeOffset.Now:O} MUX_CONFIGURED_SHAPE_FAILED{Environment.NewLine}{ex}{Environment.NewLine}");
            LogException("Device smoke configured MUX shape", ex);
            return 33;
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
