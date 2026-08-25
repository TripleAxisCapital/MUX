using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MUX.Core.Models;

namespace MUX.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<int, Key> _defaultKeys = new();
    private readonly HashSet<int> _registeredIds = new();

    public HotkeyService(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("Unable to attach global hotkeys to the MUX window.");
        _source.AddHook(WndProc);
    }

    public bool Register(int id, Key key, Action action)
    {
        _actions[id] = action;
        _defaultKeys[id] = key;
        return RegisterNative(id, ShortcutBinding.CtrlAlt(key.ToString()));
    }

    public bool Reload(ShortcutSettings settings, out List<int> failedIds)
    {
        ClearNativeRegistrations();
        failedIds = new List<int>();

        foreach (var id in _actions.Keys.OrderBy(id => id))
        {
            var binding = ResolveBinding(id, settings);
            if (!RegisterNative(id, binding))
            {
                failedIds.Add(id);
            }
        }

        return failedIds.Count == 0;
    }

    private ShortcutBinding ResolveBinding(int id, ShortcutSettings settings)
    {
        return id switch
        {
            1 => settings.ToggleMaximize,
            2 => settings.PreviousMonitor,
            3 => settings.NextMonitor,
            4 => settings.EditLayout,
            _ => ShortcutBinding.CtrlAlt(_defaultKeys.TryGetValue(id, out var key) ? key.ToString() : "M")
        };
    }

    private bool RegisterNative(int id, ShortcutBinding binding)
    {
        if (!Enum.TryParse<Key>(binding.Key, ignoreCase: true, out var key) || key == Key.None)
        {
            return false;
        }

        var modifiers = ModNoRepeat;
        if (binding.Control) modifiers |= ModControl;
        if (binding.Alt) modifiers |= ModAlt;
        if (binding.Shift) modifiers |= ModShift;
        if (binding.Windows) modifiers |= ModWin;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || !RegisterHotKey(_hwnd, id, modifiers, virtualKey))
        {
            return false;
        }

        _registeredIds.Add(id);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ClearNativeRegistrations()
    {
        foreach (var id in _registeredIds.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registeredIds.Clear();
    }

    public void Dispose()
    {
        ClearNativeRegistrations();
        _actions.Clear();
        _defaultKeys.Clear();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
