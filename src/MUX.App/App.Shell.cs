using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MUX.App.Services;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App
{
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

    private void ApplyWindowIconSafely(Window window)
    {
        try
        {
            // WPF uses the PNG only for XAML rendering. The taskbar and Windows
            // Alt+Tab need native HICON handles, provided explicitly below.
            window.Icon = BitmapFrame.Create(MuxLogoUri);
        }
        catch (Exception exception)
        {
            LogFailure("WindowIcon", exception);
        }

        // Register before Show() or EnsureHandle() so a background launch also
        // receives its icons as soon as the hidden message window is created.
        window.SourceInitialized += (_, _) => ApplyNativeWindowIconsSafely(window);
        window.Loaded += (_, _) => ApplyNativeWindowIconsSafely(window);
    }

    private void ApplyNativeWindowIconsSafely(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _smallWindowIcon ??= LoadMuxIconAtSize(new System.Drawing.Size(16, 16));
            _largeWindowIcon ??= LoadMuxIconAtSize(new System.Drawing.Size(32, 32));
            SendMessage(hwnd, 0x0080, IntPtr.Zero, _smallWindowIcon.Handle); // WM_SETICON / ICON_SMALL
            SendMessage(hwnd, 0x0080, new IntPtr(1), _largeWindowIcon.Handle); // WM_SETICON / ICON_BIG
        }
        catch (Exception exception)
        {
            LogFailure("NativeWindowIcon", exception);
        }
    }

    private static Icon LoadMuxIconAtSize(System.Drawing.Size size)
    {
        var resource = Application.GetResourceStream(MuxIconUri)
            ?? throw new InvalidOperationException("The native MUX icon was not embedded in the application.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, size);
        return (Icon)icon.Clone();
    }

    private void InitializeTrayIconSafely()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip
            {
                BackColor = Color.FromArgb(22, 22, 22),
                ForeColor = Color.FromArgb(241, 241, 241),
                Renderer = new Forms.ToolStripProfessionalRenderer(new MuxTrayColorTable()),
                Font = new Font("Segoe UI", 9.0f)
            };

            var openItem = menu.Items.Add("Open MUX", null, (_, _) => ShowMainWindow());
            openItem.ForeColor = Color.FromArgb(241, 241, 241);
            openItem.BackColor = Color.FromArgb(22, 22, 22);
            menu.Items.Add(new Forms.ToolStripSeparator());

            _predefinedAreasMenuItem = new Forms.ToolStripMenuItem("Predefined window areas")
            {
                CheckOnClick = false,
                ForeColor = Color.FromArgb(241, 241, 241),
                BackColor = Color.FromArgb(22, 22, 22),
                ToolTipText = "Turn MUX monitor regions on or off without deleting your layouts."
            };
            _predefinedAreasMenuItem.Click += PredefinedAreasMenuItem_Click;
            menu.Items.Add(_predefinedAreasMenuItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var quitItem = menu.Items.Add("Quit MUX", null, (_, _) => Quit());
            quitItem.ForeColor = Color.FromArgb(241, 241, 241);
            quitItem.BackColor = Color.FromArgb(22, 22, 22);

            _muxIcon = LoadMuxIcon();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _muxIcon,
                Text = "MUX · Ready",
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
                ? "MUX · Window areas on"
                : "MUX · Freeform mode";
        }
    }

    private static Icon LoadMuxIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(MuxIconUri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var embeddedIcon = new Icon(stream);
                // Clone before releasing the WPF resource stream. Use the actual
                // multi-resolution ICO instead of GDI+ PNG -> HICON conversion.
                return (Icon)embeddedIcon.Clone();
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

    private void RepairStartMenuShortcutSafely()
    {
        if (_updateService?.IsManagedInstall != true)
        {
            return;
        }

        try
        {
            StartMenuShortcutService.Repair();
        }
        catch (Exception exception)
        {
            // A broken or locked-down shortcut must never interfere with launch.
            LogFailure("StartMenuShortcut", exception);
        }
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

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed class MuxTrayColorTable : Forms.ProfessionalColorTable
    {
        public MuxTrayColorTable()
        {
            UseSystemColors = false;
        }

        private static readonly Color Background = Color.FromArgb(22, 22, 22);
        private static readonly Color Raised = Color.FromArgb(42, 42, 42);
        private static readonly Color Border = Color.FromArgb(62, 62, 62);

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

}
