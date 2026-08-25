using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Icon? _muxIcon;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit MUX", null, (_, _) => Quit());

        _muxIcon = CreateMuxIcon();
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

    private static Icon CreateMuxIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(10, 10, 11));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 34, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(245, 245, 247));
            var text = "M";
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, brush, (64 - size.Width) / 2f, (64 - size.Height) / 2f - 1f);
        }

        var handle = bitmap.GetHicon();
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
