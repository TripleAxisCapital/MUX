using System.Drawing;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App : Application
{
    private static readonly Uri MuxIconUri = new("pack://application:,,,/Assets/mux-app.ico", UriKind.Absolute);

    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Icon? _muxIcon;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow
        {
            Icon = BitmapFrame.Create(MuxIconUri)
        };
        MainWindow = _mainWindow;
        _mainWindow.InitializeFeatureControls();
        _mainWindow.InitializePhantomWindows();
        _mainWindow.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit MUX", null, (_, _) => Quit());

        _muxIcon = LoadMuxIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _muxIcon,
            Text = "MUX — One display. Many.",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
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

    private static Icon LoadMuxIcon()
    {
        var resource = Application.GetResourceStream(MuxIconUri)
            ?? throw new InvalidOperationException("MUX icon resource could not be loaded.");

        using var stream = resource.Stream;
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
