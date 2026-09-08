using System.Runtime.InteropServices;
using System.Text;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Arranges every eligible top-level window on the physical display under the cursor into a
/// centered, gap-free grid at one calibrated physical diagonal, then links the resulting touching
/// cluster as a rigid group. All sizing is based on MUX's calibrated display profile rather than
/// Windows DPI.
/// </summary>
public sealed class WindowAutoArrangeService
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint MonitorDefaultToNull = 0x00000000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwRestore = 9;
    private const int PlacementPasses = 4;
    private const int PixelTolerance = 2;

    public AutoArrangeResult ArrangeAtCursor(IReadOnlyList<DisplayProfile> displays, double diagonalInches)
    {
        if (displays is null || displays.Count == 0)
        {
            return AutoArrangeResult.Fail("MUX has not detected a physical display yet.");
        }

        if (diagonalInches is < 5 or > 100 || double.IsNaN(diagonalInches) || double.IsInfinity(diagonalInches))
        {
            return AutoArrangeResult.Fail("Choose an arrange size between 5 and 100 inches.");
        }

        if (!GetCursorPos(out var cursor))
        {
            return AutoArrangeResult.Fail("MUX could not determine which display the cursor is on.");
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return AutoArrangeResult.Fail("MUX could not determine which display the cursor is on.");
        }

        var monitorInfo = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return AutoArrangeResult.Fail("MUX could not read the target display work area.");
        }

        var display = FindDisplayProfile(displays, cursor, monitorInfo.RcMonitor);
        if (display is null)
        {
            return AutoArrangeResult.Fail("This display has no MUX physical-size profile yet.");
        }

        PixelSize targetSize;
        try
        {
            targetSize = DisplayGeometry.PixelsFromPhysicalDiagonal(display, diagonalInches, 16d, 9d);
        }
        catch
        {
            return AutoArrangeResult.Fail("MUX could not calculate the selected physical window size.");
        }

        var windows = CaptureWindowsOnMonitor(monitor);
        if (windows.Count == 0)
        {
            return AutoArrangeResult.Fail("There are no normal windows to arrange on this display.");
        }

        var work = monitorInfo.RcWork;
        var workWidth = work.Width;
        var workHeight = work.Height;
        var maxColumns = targetSize.Width > 0 ? workWidth / targetSize.Width : 0;
        var maxRows = targetSize.Height > 0 ? workHeight / targetSize.Height : 0;

        if (maxColumns <= 0 || maxRows <= 0 || (long)maxColumns * maxRows < windows.Count)
        {
            return AutoArrangeResult.Fail(
                $"{windows.Count} windows cannot all fit at {diagonalInches:0.#} in on this display. Choose a smaller Arrange size.");
        }

        var columns = ChooseColumnCount(windows.Count, maxColumns, maxRows, targetSize, workWidth, workHeight);
        if (columns <= 0)
        {
            return AutoArrangeResult.Fail("MUX could not find a formation that fits this display.");
        }

        var rows = (int)Math.Ceiling(windows.Count / (double)columns);
        var formationWidth = Math.Min(columns, windows.Count) * targetSize.Width;
        var formationHeight = rows * targetSize.Height;
        var originLeft = work.Left + (workWidth - formationWidth) / 2;
        var originTop = work.Top + (workHeight - formationHeight) / 2;

        var handles = windows.Select(window => window.Hwnd).ToList();
        DetachExistingGroups(handles);

        var placementFailures = 0;
        for (var index = 0; index < windows.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var desiredLeft = originLeft + column * targetSize.Width;
            var desiredTop = originTop + row * targetSize.Height;

            if (!PlaceWindowAtVisualRect(
                    windows[index].Hwnd,
                    desiredLeft,
                    desiredTop,
                    targetSize.Width,
                    targetSize.Height))
            {
                placementFailures++;
            }
        }

        if (placementFailures > 0)
        {
            return AutoArrangeResult.Fail(
                $"MUX arranged the display, but {placementFailures} window(s) refused the exact requested size.");
        }

        var linked = true;
        if (handles.Count > 1)
        {
            var linkService = ReliableWindowLinkService.Shared;
            linkService.GroupSnappingEnabled = true;
            linked = linkService.ToggleLink(handles[0]);
        }

        return linked || handles.Count == 1
            ? AutoArrangeResult.Ok(
                windows.Count,
                display.FriendlyName,
                diagonalInches,
                $"Arranged and linked {windows.Count} window{(windows.Count == 1 ? string.Empty : "s")} at {diagonalInches:0.#} in.")
            : AutoArrangeResult.Fail(
                $"The windows were arranged at {diagonalInches:0.#} in, but MUX could not link the formation.");
    }

    private static DisplayProfile? FindDisplayProfile(
        IReadOnlyList<DisplayProfile> displays,
        NativePoint cursor,
        NativeRect monitorRect)
    {
        var byCursor = displays.FirstOrDefault(display =>
            cursor.X >= display.LeftPx &&
            cursor.X < display.LeftPx + display.WidthPx &&
            cursor.Y >= display.TopPx &&
            cursor.Y < display.TopPx + display.HeightPx);
        if (byCursor is not null)
        {
            return byCursor;
        }

        return displays
            .OrderBy(display =>
                Math.Abs(display.LeftPx - monitorRect.Left) +
                Math.Abs(display.TopPx - monitorRect.Top) +
                Math.Abs(display.WidthPx - monitorRect.Width) +
                Math.Abs(display.HeightPx - monitorRect.Height))
            .FirstOrDefault();
    }

    private static List<WindowEntry> CaptureWindowsOnMonitor(IntPtr monitor)
    {
        var windows = new List<WindowEntry>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsEligibleWindow(hwnd) || MonitorFromWindow(hwnd, MonitorDefaultToNull) != monitor)
            {
                return true;
            }

            if (!TryGetVisualRect(hwnd, out var visual))
            {
                return true;
            }

            windows.Add(new WindowEntry(hwnd, visual));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.Visual.Top)
            .ThenBy(window => window.Visual.Left)
            .ThenBy(window => window.Hwnd.ToInt64())
            .ToList();
    }

    private static int ChooseColumnCount(
        int count,
        int maxColumns,
        int maxRows,
        PixelSize target,
        int workWidth,
        int workHeight)
    {
        var workAspect = workHeight > 0 ? workWidth / (double)workHeight : 1d;
        var bestColumns = 0;
        var bestScore = double.MaxValue;

        for (var columns = 1; columns <= Math.Min(count, maxColumns); columns++)
        {
            var rows = (int)Math.Ceiling(count / (double)columns);
            if (rows > maxRows)
            {
                continue;
            }

            var emptyCells = rows * columns - count;
            var formationWidth = Math.Min(columns, count) * target.Width;
            var formationHeight = rows * target.Height;
            var formationAspect = formationHeight > 0 ? formationWidth / (double)formationHeight : 1d;
            var aspectPenalty = Math.Abs(Math.Log(Math.Max(0.0001, formationAspect / Math.Max(0.0001, workAspect))));
            var score = emptyCells * 1000d + aspectPenalty * 100d + rows * 0.01d;
            if (score < bestScore)
            {
                bestScore = score;
                bestColumns = columns;
            }
        }

        return bestColumns;
    }

    private static void DetachExistingGroups(IEnumerable<IntPtr> handles)
    {
        var linkService = ReliableWindowLinkService.Shared;
        var visitedGroups = new HashSet<long>();

        foreach (var hwnd in handles)
        {
            if (!linkService.IsLinked(hwnd))
            {
                continue;
            }

            var members = linkService.GetGroupMembers(hwnd);
            var groupKey = members.Count == 0
                ? hwnd.ToInt64()
                : members.Min(member => member.ToInt64());
            if (!visitedGroups.Add(groupKey))
            {
                continue;
            }

            for (var attempt = 0; attempt < 4 && linkService.IsLinked(hwnd); attempt++)
            {
                linkService.ToggleLink(hwnd);
            }
        }
    }

    private static bool PlaceWindowAtVisualRect(
        IntPtr hwnd,
        int desiredVisualLeft,
        int desiredVisualTop,
        int desiredVisualWidth,
        int desiredVisualHeight)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            ShowWindow(hwnd, SwRestore);
        }
        catch
        {
        }

        for (var pass = 0; pass < PlacementPasses; pass++)
        {
            if (!TryGetWindowGeometry(hwnd, out var raw, out var visual))
            {
                return false;
            }

            var leftInset = visual.Left - raw.Left;
            var topInset = visual.Top - raw.Top;
            var widthInset = raw.Width - visual.Width;
            var heightInset = raw.Height - visual.Height;

            var desiredRawLeft = desiredVisualLeft - leftInset;
            var desiredRawTop = desiredVisualTop - topInset;
            var desiredRawWidth = Math.Max(1, desiredVisualWidth + widthInset);
            var desiredRawHeight = Math.Max(1, desiredVisualHeight + heightInset);

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                desiredRawLeft,
                desiredRawTop,
                desiredRawWidth,
                desiredRawHeight,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

            if (!TryGetVisualRect(hwnd, out var result))
            {
                continue;
            }

            if (Math.Abs(result.Left - desiredVisualLeft) <= PixelTolerance &&
                Math.Abs(result.Top - desiredVisualTop) <= PixelTolerance &&
                Math.Abs(result.Width - desiredVisualWidth) <= PixelTolerance &&
                Math.Abs(result.Height - desiredVisualHeight) <= PixelTolerance)
            {
                return true;
            }
        }

        return TryGetVisualRect(hwnd, out var finalRect) &&
               Math.Abs(finalRect.Left - desiredVisualLeft) <= PixelTolerance &&
               Math.Abs(finalRect.Top - desiredVisualTop) <= PixelTolerance &&
               Math.Abs(finalRect.Width - desiredVisualWidth) <= PixelTolerance &&
               Math.Abs(finalRect.Height - desiredVisualHeight) <= PixelTolerance;
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        if ((GetWindowExStyle(hwnd) & WsExToolWindow) != 0 || IsWindowCloaked(hwnd))
        {
            return false;
        }

        var className = GetWindowClassName(hwnd);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
        {
            return false;
        }

        return TryGetVisualRect(hwnd, out var rect) && rect.Width >= 80 && rect.Height >= 60;
    }

    private static bool TryGetWindowGeometry(IntPtr hwnd, out NativeRect raw, out NativeRect visual)
    {
        raw = default;
        visual = default;
        if (!GetWindowRect(hwnd, out raw) || raw.Width <= 0 || raw.Height <= 0)
        {
            return false;
        }

        if (!TryGetVisualRect(hwnd, out visual))
        {
            visual = raw;
        }

        return visual.Width > 0 && visual.Height > 0;
    }

    private static bool TryGetVisualRect(IntPtr hwnd, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0 &&
            rect.Width > 0 && rect.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        var cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static long GetWindowExStyle(IntPtr hwnd)
        => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() : GetWindowLong(hwnd, GwlExStyle);

    private readonly record struct WindowEntry(IntPtr Hwnd, NativeRect Visual);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;
    }

    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

public readonly record struct AutoArrangeResult(
    bool Success,
    int WindowCount,
    string DisplayName,
    double DiagonalInches,
    string Message)
{
    public static AutoArrangeResult Ok(int windowCount, string displayName, double diagonalInches, string message)
        => new(true, windowCount, displayName, diagonalInches, message);

    public static AutoArrangeResult Fail(string message)
        => new(false, 0, string.Empty, 0, message);
}
