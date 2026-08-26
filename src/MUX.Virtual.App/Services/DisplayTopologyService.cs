using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class DisplayTopologyService
{
    private const int EnumCurrentSettings = -1;
    private const int DispChangeSuccessful = 0;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsNoReset = 0x10000000;

    private const uint DmPosition = 0x00000020;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;

    private const int DisplayDeviceAttachedToDesktop = 0x00000001;

    public async Task<IReadOnlyList<ActiveVirtualMonitor>> ConfigureAsync(
        IReadOnlyList<VirtualMonitorPlan> plans,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DisplayInfo> virtualDisplays = Array.Empty<DisplayInfo>();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            virtualDisplays = EnumerateDisplays()
                .Where(x => IsMuxVirtual(x.DeviceString))
                .ToList();

            if (virtualDisplays.Count >= plans.Count)
            {
                break;
            }

            await Task.Delay(200, cancellationToken);
        }

        if (virtualDisplays.Count < plans.Count)
        {
            throw new InvalidOperationException(
                $"Windows created only {virtualDisplays.Count} of {plans.Count} MUX virtual display targets. Make sure the bundled MUX Virtual driver is installed and production-signed for this PC.");
        }

        var allDisplays = EnumerateDisplays();
        var nonMuxActive = allDisplays
            .Where(x =>
                !IsMuxVirtual(x.DeviceString) &&
                x.IsAttached &&
                x.CurrentMode is not null)
            .ToList();

        var rightEdge = nonMuxActive.Count == 0
            ? 0
            : nonMuxActive.Max(x => x.CurrentMode!.Value.Left + x.CurrentMode.Value.Width);

        var virtualCandidates = virtualDisplays
            .OrderBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Take(plans.Count)
            .ToList();

        var xPosition = rightEdge + 64;

        for (var i = 0; i < plans.Count; i++)
        {
            var display = virtualCandidates[i];
            var plan = plans[i];
            ApplyMode(
                display.DeviceName,
                xPosition,
                0,
                plan.Width,
                plan.Height,
                plan.RefreshRate);
            xPosition += plan.Width;
        }

        var applyResult = ChangeDisplaySettingsEx(
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            IntPtr.Zero);

        if (applyResult != DispChangeSuccessful)
        {
            throw new InvalidOperationException(
                $"Windows rejected the MUX virtual display topology (ChangeDisplaySettingsEx result {applyResult}).");
        }

        await Task.Delay(750, cancellationToken);

        var refreshed = EnumerateDisplays()
            .Where(x =>
                IsMuxVirtual(x.DeviceString) &&
                x.IsAttached &&
                x.CurrentMode is not null)
            .OrderBy(x => x.CurrentMode!.Value.Left)
            .ToList();

        if (refreshed.Count < plans.Count)
        {
            throw new InvalidOperationException(
                "Windows did not attach all MUX virtual monitors to the desktop.");
        }

        var result = new List<ActiveVirtualMonitor>(plans.Count);
        for (var i = 0; i < plans.Count; i++)
        {
            var mode = refreshed[i].CurrentMode!.Value;
            result.Add(new ActiveVirtualMonitor(
                plans[i],
                refreshed[i].DeviceName,
                new ScreenRect(mode.Left, mode.Top, mode.Width, mode.Height)));
        }

        return result;
    }

    private static bool IsMuxVirtual(string text) =>
        text.Contains("MUX Virtual", StringComparison.OrdinalIgnoreCase);

    private static void ApplyMode(
        string deviceName,
        int left,
        int top,
        int width,
        int height,
        uint refreshRate)
    {
        var mode = DevMode.Create();
        mode.dmFields =
            DmPosition |
            DmPelsWidth |
            DmPelsHeight |
            DmDisplayFrequency;

        mode.dmPositionX = left;
        mode.dmPositionY = top;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        mode.dmDisplayFrequency = refreshRate;

        var result = ChangeDisplaySettingsEx(
            deviceName,
            ref mode,
            IntPtr.Zero,
            CdsUpdateRegistry | CdsNoReset,
            IntPtr.Zero);

        if (result != DispChangeSuccessful)
        {
            throw new InvalidOperationException(
                $"Windows rejected mode {width}×{height} for {deviceName} (result {result}).");
        }
    }

    private static List<DisplayInfo> EnumerateDisplays()
    {
        var result = new List<DisplayInfo>();

        for (uint index = 0; ; index++)
        {
            var device = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            var mode = DevMode.Create();
            ModeRect? current = null;

            if (EnumDisplaySettingsEx(
                device.DeviceName,
                EnumCurrentSettings,
                ref mode,
                0))
            {
                current = new ModeRect(
                    mode.dmPositionX,
                    mode.dmPositionY,
                    (int)mode.dmPelsWidth,
                    (int)mode.dmPelsHeight);
            }

            result.Add(new DisplayInfo(
                device.DeviceName,
                device.DeviceString,
                (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0,
                current));
        }

        return result;
    }

    private sealed record DisplayInfo(
        string DeviceName,
        string DeviceString,
        bool IsAttached,
        ModeRect? CurrentMode);

    private readonly record struct ModeRect(
        int Left,
        int Top,
        int Width,
        int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DisplayDevice Create() => new()
        {
            cb = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;

        public static DevMode Create() => new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DevMode>()
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(
        string lpszDeviceName,
        int iModeNum,
        ref DevMode lpDevMode,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DevMode lpDevMode,
        IntPtr hwnd,
        uint dwFlags,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        IntPtr lpDevMode,
        IntPtr hwnd,
        uint dwFlags,
        IntPtr lParam);
}
