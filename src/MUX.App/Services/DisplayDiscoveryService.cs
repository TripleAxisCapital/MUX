using System.Runtime.InteropServices;
using MUX.Core.Models;

namespace MUX.App.Services;

public sealed class DisplayDiscoveryService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int CchDeviceName = 32;
    private const int CchDeviceString = 128;

    public IReadOnlyList<DisplayProfile> GetDisplays()
    {
        var result = new List<DisplayProfile>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                CbSize = Marshal.SizeOf<MonitorInfoEx>(),
                SzDevice = string.Empty
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var friendlyName = info.SzDevice;
            var device = new DisplayDevice
            {
                Cb = Marshal.SizeOf<DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty
            };

            if (EnumDisplayDevices(info.SzDevice, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceString))
            {
                friendlyName = device.DeviceString;
            }

            result.Add(new DisplayProfile
            {
                DeviceName = info.SzDevice,
                FriendlyName = friendlyName,
                LeftPx = info.RcMonitor.Left,
                TopPx = info.RcMonitor.Top,
                WidthPx = info.RcMonitor.Right - info.RcMonitor.Left,
                HeightPx = info.RcMonitor.Bottom - info.RcMonitor.Top,
                IsPrimary = (info.DwFlags & MonitorInfoPrimary) != 0
            });

            return true;
        }, IntPtr.Zero);

        return result
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.LeftPx)
            .ThenBy(x => x.TopPx)
            .ToList();
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string SzDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceString)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceString)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceString)]
        public string DeviceKey;
    }
}
