namespace MUX.Virtual.App;

public partial class App : Application
{
    private static readonly string LogRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MUX",
        "Virtual");

    private static readonly string StartupLogPath =
        Path.Combine(LogRoot, "startup.log");

    private static readonly string StartupErrorLogPath =
        Path.Combine(LogRoot, "startup-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogException(
                "AppDomain.UnhandledException",
                args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogException(
                "DispatcherUnhandledException",
                args.Exception);

            TryShowError(
                "MUX Virtual encountered an unexpected error.",
                args.Exception);

            // Do not let a recoverable UI exception make the application vanish
            // without feedback. The error is visible and persisted for support.
            args.Handled = true;
        };

        try
        {
            WriteStartupLog("Process started.");
            base.OnStartup(e);

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
            TryShowError(
                "MUX Virtual could not start correctly.",
                ex);
            Shutdown(-1);
        }
    }

    internal static void LogException(
        string stage,
        Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(
                StartupErrorLogPath,
                $"{DateTimeOffset.Now:O} [{stage}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    internal static void WriteStartupLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(
                StartupLogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void TryShowError(
        string heading,
        Exception exception)
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
        catch
        {
        }
    }
}
