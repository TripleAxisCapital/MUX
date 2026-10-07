using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MUX.App.Services;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App : Application
{
    private static readonly Uri MuxLogoUri = new("pack://application:,,,/Assets/mux-logo.png", UriKind.Absolute);

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _predefinedAreasMenuItem;
    private MainWindow? _mainWindow;
    private Icon? _muxIcon;
    private StreamDeckCommandInbox? _streamDeckCommandInbox;
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

        // Keep the legacy Win32 bridges for compatibility with explicit CLI commands, but make
        // sure the black-bars bridge is actually attached. Stream Deck itself uses the more robust
        // LocalAppData command inbox below and therefore does not depend on a visible HWND.
        _mainWindow.InitializeBlackBarsCommandBridge();
        _mainWindow.InitializeScreenEdgeBars();

        _streamDeckCommandInbox = new StreamDeckCommandInbox(
            _mainWindow.Dispatcher,
            _mainWindow.QueueAutoArrangeCommand,
            () => _mainWindow.ToggleAllBlackBars());
        _streamDeckCommandInbox.Start();

        _mainWindow.PredefinedAreasEnabledChanged += MainWindow_PredefinedAreasEnabledChanged;
        _mainWindow.Show();

        InitializeTrayIconSafely();
        InitializeAutoArrangeTrayControls();
        InitializeBlackBarsTrayControls();
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
        _streamDeckCommandInbox?.Dispose();
        _streamDeckCommandInbox = null;
        DisposeBlackBarsTrayControls();
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
        _streamDeckCommandInbox?.Dispose();
        _streamDeckCommandInbox = null;
        DisposeBlackBarsTrayControls();
        try { ScreenEdgeBarService.Shared.Dispose(); } catch { }
        try { EnhancedEdgeCoverService.Shared.Dispose(); } catch { }
        _trayIcon?.Dispose();
        _muxIcon?.Dispose();
        base.OnExit(e);
    }

}
