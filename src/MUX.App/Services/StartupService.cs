using Microsoft.Win32;

namespace MUX.App.Services;

/// <summary>
/// Keeps Windows sign-in pointed at MUX's stable installed launcher.
/// Velopack executes the app inside a replaceable "current" directory.
/// </summary>
public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "MUX";

    public bool IsEnabled()
    {
        if (IsDisabledInWindowsSettings())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var stored = key?.GetValue(ValueName) as string;
        return File.Exists(GetLaunchExecutable())
            && string.Equals(stored, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Repairs stale registry entries and restores saved opt-in, while
    /// respecting an explicit disable in Windows Startup Apps.
    /// </summary>
    public bool Reconcile(bool savedPreference)
    {
        if (IsDisabledInWindowsSettings())
        {
            return false;
        }

        if (savedPreference || HasRunEntry())
        {
            SetEnabled(true);
        }

        return IsEnabled();
    }

    public void SetEnabled(bool enabled)
    {
        var executable = GetLaunchExecutable();
        if (enabled && !File.Exists(executable))
        {
            throw new FileNotFoundException("MUX could not find its startup launcher. Reinstall the managed MUX Setup.", executable);
        }

        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            if (enabled)
            {
                key.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        // Windows tracks disabled startup programs separately from the Run key.
        // Enabling the checkbox explicitly opts back in.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true)
                             ?? (enabled ? Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true) : null);
        if (enabled)
        {
            var data = new byte[12];
            data[0] = 2;
            approved?.SetValue(ValueName, data, RegistryValueKind.Binary);
        }
        else
        {
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        if (IsEnabled() != enabled)
        {
            throw new InvalidOperationException("Windows did not accept the MUX sign-in setting.");
        }
    }

    private static bool HasRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsDisabledInWindowsSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
        return key?.GetValue(ValueName) is byte[] value && value.Length > 0 && value[0] == 3;
    }

    private static string GetLaunchCommand() => $"\"{GetLaunchExecutable()}\" --background";

    private static string GetLaunchExecutable()
    {
        var runningExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to locate MUX.exe.");

        var directory = Path.GetDirectoryName(runningExe);
        if (directory is not null &&
            string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Directory.GetParent(directory)?.FullName;
            if (root is not null && File.Exists(Path.Combine(root, "Update.exe")))
            {
                var stableLauncher = Path.Combine(root, "MUX.exe");
                if (File.Exists(stableLauncher))
                {
                    return stableLauncher;
                }
            }
        }

        // Legacy and portable MUX run from their own executable directory.
        return runningExe;
    }
}
