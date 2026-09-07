using System.Diagnostics;
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
        RetireStaleStandardInstances();

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

    private static void RetireStaleStandardInstances()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (!string.Equals(Path.GetFileName(executablePath), "MUX.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (process.HasExited)
                    {
                        continue;
                    }

                    // Closing MUX normally hides it to the tray, so first give the old process
                    // a chance to react and then terminate it if it remains resident. This prevents
                    // an older download from continuing to own the global resize pill after a new
                    // Standard build is launched.
                    process.CloseMainWindow();
                    if (!process.WaitForExit(400))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(1200);
                    }
                }
                catch (Exception exception)
                {
                    LogFailure("RetireStaleStandardInstance", exception);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            LogFailure("RetireStaleStandardInstances", exception);
        }
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
            var menu = new Forms.ContextMenuStrip
            {
                BackColor = Color.FromArgb(28, 28, 31),
                ForeColor = Color.FromArgb(245, 245, 247),
                Renderer = new Forms.ToolStripProfessionalRenderer(new MuxTrayColorTable()),
                Font = new Font("Segoe UI", 9.0f)
            };

            var openItem = menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
            openItem.ForeColor = Color.FromArgb(245, 245, 247);
            openItem.BackColor = Color.FromArgb(28, 28, 31);
            menu.Items.Add(new Forms.ToolStripSeparator());

            _predefinedAreasMenuItem = new Forms.ToolStripMenuItem("Predefined window areas")
            {
                CheckOnClick = false,
                ForeColor = Color.FromArgb(245, 245, 247),
                BackColor = Color.FromArgb(28, 28, 31),
                ToolTipText = "Turn MUX monitor regions on or off without deleting your layouts."
            };
            _predefinedAreasMenuItem.Click += PredefinedAreasMenuItem_Click;
            menu.Items.Add(_predefinedAreasMenuItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var quitItem = menu.Items.Add("Quit MUX", null, (_, _) => Quit());
            quitItem.ForeColor = Color.FromArgb(245, 245, 247);
            quitItem.BackColor = Color.FromArgb(28, 28, 31);

            _muxIcon = LoadMuxIcon();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _muxIcon,
                Text = "MUX Standard — physical-inch sizing ready",
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
                ? "MUX Standard — Areas on · inch sizing"
                : "MUX Standard — Freeform · inch sizing";
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

    private sealed class MuxTrayColorTable : Forms.ProfessionalColorTable
    {
        public MuxTrayColorTable()
        {
            UseSystemColors = false;
        }

        private static readonly Color Background = Color.FromArgb(28, 28, 31);
        private static readonly Color Raised = Color.FromArgb(44, 44, 49);
        private static readonly Color Border = Color.FromArgb(58, 58, 64);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Raised;
        public override Color MenuItemSelectedGradientBegin => Raised;
        public override Color MenuItemSelectedGradientEnd => Raised;
        public override Color MenuItemPressedGradientBegin => Raised;
        public override Color MenuItemPressedGradientMiddle => Raised;
        public override Color MenuItemPressedGradientEnd => Raised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Raised;
        public override Color CheckSelectedBackground => Raised;
        public override Color CheckPressedBackground => Raised;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
