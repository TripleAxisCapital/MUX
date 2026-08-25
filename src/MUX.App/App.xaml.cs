using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Icon? _muxIcon;
    private bool _isExiting;

    public App()
    {
        DispatcherUnhandledException += (_, args) => LogFailure("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogFailure("AppDomain", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => LogFailure("TaskScheduler", args.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.InitializeFeatureControls();
        _mainWindow.InitializePhantomWindows();
        _mainWindow.Show();

        InitializeTrayIconSafely();
    }

    public bool IsExiting => _isExiting;

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    public void Quit()
    {
        _isExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _muxIcon?.Dispose();
        _muxIcon = null;
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _muxIcon?.Dispose();
        base.OnExit(e);
    }

    private void InitializeTrayIconSafely()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit MUX", null, (_, _) => Quit());

            _muxIcon = LoadMuxIconFromExecutable();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _muxIcon,
                Text = "MUX — One display. Many.",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        }
        catch (Exception exception)
        {
            // Branding/tray integration must never prevent the main MUX window from running.
            LogFailure("TrayIcon", exception);
            _trayIcon?.Dispose();
            _trayIcon = null;
            _muxIcon?.Dispose();
            _muxIcon = null;
        }
    }

    private static Icon LoadMuxIconFromExecutable()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                using var associated = Icon.ExtractAssociatedIcon(executablePath);
                if (associated is not null)
                {
                    return (Icon)associated.Clone();
                }
            }
        }
        catch (Exception exception)
        {
            LogFailure("ExecutableIcon", exception);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static void LogFailure(string stage, Exception exception)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MUX");
            Directory.CreateDirectory(root);
            var logPath = Path.Combine(root, "startup-error.log");
            var entry = $"[{DateTimeOffset.Now:O}] {stage}\r\n{exception}\r\n\r\n";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Never allow diagnostic logging itself to become a startup failure.
        }
    }
}
