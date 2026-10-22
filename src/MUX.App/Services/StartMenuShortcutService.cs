using Velopack.Windows;

namespace MUX.App.Services;

/// <summary>
/// Repairs the per-user Start Menu entry after migrating from legacy installers.
/// A stale or unbranded .lnk may be indexed by Windows Search even though MUX.exe
/// and the managed installer already carry the correct multi-size application icon.
/// </summary>
internal static class StartMenuShortcutService
{
    internal static void Repair()
    {
        var launcher = StartupService.GetLaunchExecutable();
        if (!File.Exists(launcher))
        {
            return;
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs))
        {
            return;
        }

        var linkFile = Path.Combine(programs, "MUX.lnk");
        if (File.Exists(linkFile))
        {
            try
            {
                using var existing = new ShellLink(linkFile);
                if (string.Equals(existing.Target, launcher, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.IconPath, launcher, StringComparison.OrdinalIgnoreCase)
                    && existing.IconIndex == 0)
                {
                    return;
                }
            }
            catch
            {
                // Invalid legacy links are replaced rather than blocking startup.
            }
        }

        Directory.CreateDirectory(programs);
        using var shortcut = new ShellLink
        {
            Target = launcher,
            WorkingDirectory = Path.GetDirectoryName(launcher) ?? programs,
            IconPath = launcher,
            IconIndex = 0,
            Description = "MUX workspace manager",
            ShortCutFile = linkFile
        };
        shortcut.Save();
    }
}
