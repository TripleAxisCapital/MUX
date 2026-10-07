using System.Drawing;
using MUX.App.Services;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App
{
    private Forms.ToolStripMenuItem? _windowBlackBarsMenuItem;
    private Forms.ToolStripMenuItem? _screenEdgeBarsMenuItem;

    private void InitializeBlackBarsTrayControls()
    {
        if (_windowBlackBarsMenuItem is not null || _trayIcon?.ContextMenuStrip is not { } menu || _mainWindow is null)
        {
            return;
        }

        _windowBlackBarsMenuItem = CreateTrayItem("Window black bars", WindowBlackBarsTrayItem_Click);
        _screenEdgeBarsMenuItem = CreateTrayItem("Screen edge bars", ScreenEdgeBarsTrayItem_Click);

        var insertionIndex = Math.Max(0, menu.Items.Count - 2);
        menu.Items.Insert(insertionIndex++, _windowBlackBarsMenuItem);
        menu.Items.Insert(insertionIndex, _screenEdgeBarsMenuItem);
        menu.Opening += BlackBarsTrayMenu_Opening;
        UpdateBlackBarsTrayState();
    }

    private static Forms.ToolStripMenuItem CreateTrayItem(string text, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            CheckOnClick = false,
            ForeColor = Color.FromArgb(245, 245, 247),
            BackColor = Color.FromArgb(28, 28, 31)
        };
        item.Click += handler;
        return item;
    }

    private void WindowBlackBarsTrayItem_Click(object? sender, EventArgs e)
    {
        _mainWindow?.ToggleAllBlackBars();
        UpdateBlackBarsTrayState();
    }

    private void ScreenEdgeBarsTrayItem_Click(object? sender, EventArgs e)
    {
        _mainWindow?.ToggleScreenEdgeBars();
        UpdateBlackBarsTrayState();
    }

    private void BlackBarsTrayMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => UpdateBlackBarsTrayState();

    private void UpdateBlackBarsTrayState()
    {
        var windowBars = EnhancedEdgeCoverService.Shared;
        var screenBars = ScreenEdgeBarService.Shared;

        if (_windowBlackBarsMenuItem is not null)
        {
            _windowBlackBarsMenuItem.Checked = windowBars.AreCoversGloballyVisible;
            _windowBlackBarsMenuItem.Text = windowBars.AreCoversGloballyVisible
                ? $"Window black bars · Shown ({windowBars.ConfiguredWindowCount})"
                : $"Window black bars · Hidden ({windowBars.ConfiguredWindowCount})";
            _windowBlackBarsMenuItem.ToolTipText = "Show or hide all configured per-window black bars without losing their saved depths.";
        }

        if (_screenEdgeBarsMenuItem is not null)
        {
            _screenEdgeBarsMenuItem.Checked = screenBars.AreBarsGloballyVisible;
            _screenEdgeBarsMenuItem.Text = screenBars.AreBarsGloballyVisible
                ? $"Screen edge bars · Shown ({screenBars.ConfiguredDisplayCount})"
                : $"Screen edge bars · Hidden ({screenBars.ConfiguredDisplayCount})";
            _screenEdgeBarsMenuItem.ToolTipText = "Show or hide the four persistent edge bars configured for physical monitors.";
        }
    }

    private void DisposeBlackBarsTrayControls()
    {
        if (_trayIcon?.ContextMenuStrip is { } menu)
        {
            try { menu.Opening -= BlackBarsTrayMenu_Opening; } catch { }
        }

        if (_windowBlackBarsMenuItem is not null)
        {
            _windowBlackBarsMenuItem.Click -= WindowBlackBarsTrayItem_Click;
            _windowBlackBarsMenuItem = null;
        }

        if (_screenEdgeBarsMenuItem is not null)
        {
            _screenEdgeBarsMenuItem.Click -= ScreenEdgeBarsTrayItem_Click;
            _screenEdgeBarsMenuItem = null;
        }
    }
}
