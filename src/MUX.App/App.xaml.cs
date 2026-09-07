using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App : Application
{
    private static readonly Uri MuxLogoUri = new("pack://application:,,,/Assets/mux-logo.png", UriKind.Absolute);

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _predefinedAreasMenuItem;
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
        ApplyWindowIconSafely(_mainWindow);
        MainWindow = _mainWindow;
        _mainWindow.InitializeFeatureControls();
        _mainWindow.InitializePhantomWindows();
        _mainWindow.InitializeFreeformControls();
        _mainWindow.PredefinedAreasEnabledChanged += MainWindow_PredefinedAreasEnabledChanged;
        _mainWindow.Show();

        InitializeTrayIconSafely();
        UpdatePredefinedAreasTrayState();
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
        _predefinedAreasMenuItem = null;
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

    private static void ApplyWindowIconSafely(Window window)
    {
        try
        {
            window.Icon = BitmapFrame.Create(MuxLogoUri);
        }
        catch (Exception exception)
        {
            // Branding must never prevent MUX from opening.
            LogFailure("WindowIcon", exception);
        }
    }

    private void InitializeTrayIconSafely()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
            menu.Items.Add(new Forms.ToolStripSeparator());

            _predefinedAreasMenuItem = new Forms.ToolStripMenuItem("Predefined window areas")
            {
                CheckOnClick = false,
                ToolTipText = "Turn MUX monitor regions on or off without deleting your layouts."
            };
            _predefinedAreasMenuItem.Click += PredefinedAreasMenuItem_Click;
            menu.Items.Add(_predefinedAreasMenuItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit MUX", null, (_, _) => Quit());

            _muxIcon = LoadMuxIcon();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _muxIcon,
                Text = "MUX — Freeform sizing ready",
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
            _predefinedAreasMenuItem = null;
            _muxIcon?.Dispose();
            _muxIcon = null;
        }
    }

    private async void PredefinedAreasMenuItem_Click(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        await _mainWindow.SetPredefinedAreasEnabledAsync(!_mainWindow.PredefinedAreasEnabled);
        UpdatePredefinedAreasTrayState();
    }

    private void MainWindow_PredefinedAreasEnabledChanged(object? sender, EventArgs e)
    {
        UpdatePredefinedAreasTrayState();
    }

    private void UpdatePredefinedAreasTrayState()
    {
        if (_mainWindow is null)
        {
            return;
        }

        var enabled = _mainWindow.PredefinedAreasEnabled;
        if (_predefinedAreasMenuItem is not null)
        {
            _predefinedAreasMenuItem.Checked = enabled;
            _predefinedAreasMenuItem.Text = enabled
                ? "Predefined window areas · On"
                : "Predefined window areas · Off";
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Text = enabled
                ? "MUX — Areas on · Freeform sizing ready"
                : "MUX — Freeform sizing · Areas off";
        }
    }

    private static Icon LoadMuxIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(MuxLogoUri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var source = new Bitmap(stream);
                using var scaled = new Bitmap(source, new System.Drawing.Size(64, 64));
                var handle = scaled.GetHicon();

                try
                {
                    using var temporary = Icon.FromHandle(handle);
                    return (Icon)temporary.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
        catch (Exception exception)
        {
            LogFailure("LogoIcon", exception);
        }

        return LoadMuxIconFromExecutable();
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
