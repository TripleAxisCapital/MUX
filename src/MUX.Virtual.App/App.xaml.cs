namespace MUX.Virtual.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            TryWriteCrashLog(args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            TryWriteCrashLog(args.Exception);
        };

        base.OnStartup(e);
    }

    private static void TryWriteCrashLog(Exception? exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MUX",
                "Virtual");

            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "startup-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        catch
        {
        }
    }
}
