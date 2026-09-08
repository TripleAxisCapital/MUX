using System.Drawing;
using Forms = System.Windows.Forms;

namespace MUX.App;

public partial class App
{
    private Forms.ToolStripMenuItem? _autoArrangeMenuItem;
    private Forms.ToolStripMenuItem? _autoArrangeSizeMenuItem;

    public void InitializeAutoArrangeTrayControls()
    {
        if (_autoArrangeMenuItem is not null || _trayIcon?.ContextMenuStrip is not { } menu || _mainWindow is null)
        {
            return;
        }

        _autoArrangeMenuItem = new Forms.ToolStripMenuItem
        {
            ForeColor = Color.FromArgb(245, 245, 247),
            BackColor = Color.FromArgb(28, 28, 31),
            ToolTipText = "Arrange, size and link every normal window on the display under the cursor."
        };
        _autoArrangeMenuItem.Click += AutoArrangeTrayItem_Click;

        _autoArrangeSizeMenuItem = new Forms.ToolStripMenuItem
        {
            Text = "Arrange size",
            ForeColor = Color.FromArgb(245, 245, 247),
            BackColor = Color.FromArgb(28, 28, 31)
        };

        foreach (var size in new double[] { 15, 17, 20, 24, 25, 27, 32, 34, 40, 43 })
        {
            var sizeItem = new Forms.ToolStripMenuItem($"{size:0.#} in")
            {
                Tag = size,
                ForeColor = Color.FromArgb(245, 245, 247),
                BackColor = Color.FromArgb(28, 28, 31),
                CheckOnClick = false
            };
            sizeItem.Click += AutoArrangeSizeTrayItem_Click;
            _autoArrangeSizeMenuItem.DropDownItems.Add(sizeItem);
        }

        var insertionIndex = Math.Max(0, menu.Items.Count - 2);
        menu.Items.Insert(insertionIndex++, _autoArrangeMenuItem);
        menu.Items.Insert(insertionIndex, _autoArrangeSizeMenuItem);

        _mainWindow.AutoArrangeSettingsChanged += MainWindow_AutoArrangeSettingsChanged;
        UpdateAutoArrangeTrayState();
    }

    private void AutoArrangeTrayItem_Click(object? sender, EventArgs e)
    {
        _mainWindow?.AutoArrangeCursorDisplay();
    }

    private async void AutoArrangeSizeTrayItem_Click(object? sender, EventArgs e)
    {
        if (_mainWindow is null || sender is not Forms.ToolStripMenuItem { Tag: double size })
        {
            return;
        }

        await _mainWindow.SetAutoArrangeSizeAsync(size);
        UpdateAutoArrangeTrayState();
    }

    private void MainWindow_AutoArrangeSettingsChanged(object? sender, EventArgs e)
    {
        UpdateAutoArrangeTrayState();
    }

    private void UpdateAutoArrangeTrayState()
    {
        if (_mainWindow is null)
        {
            return;
        }

        var selected = _mainWindow.AutoArrangeDiagonalInches;
        if (_autoArrangeMenuItem is not null)
        {
            _autoArrangeMenuItem.Text = $"Auto arrange cursor display · {selected:0.#} in";
        }

        if (_autoArrangeSizeMenuItem is not null)
        {
            foreach (var item in _autoArrangeSizeMenuItem.DropDownItems.OfType<Forms.ToolStripMenuItem>())
            {
                item.Checked = item.Tag is double size && Math.Abs(size - selected) < 0.001;
            }
        }
    }
}
