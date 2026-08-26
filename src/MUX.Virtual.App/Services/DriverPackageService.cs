namespace MUX.Virtual.App.Services;

public sealed record DriverInstallResult(bool Success, string Output);

public sealed class DriverPackageService
{
    public string DriverDirectory => Path.Combine(AppContext.BaseDirectory, "Driver");
    public string InfPath => Path.Combine(DriverDirectory, "MUXVirtualDisplay.inf");

    public bool PackageIsBundled =>
        File.Exists(InfPath) &&
        File.Exists(Path.Combine(DriverDirectory, "MUXVirtualDisplay.dll"));

    public async Task<DriverInstallResult> InstallAsync()
    {
        if (!PackageIsBundled)
        {
            return new DriverInstallResult(
                false,
                "The MUX Virtual display driver is not present beside the application. Use the MUX Virtual release ZIP, which contains the Driver folder.");
        }

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "pnputil.exe"),
            Arguments = $"/add-driver \"{InfPath}\" /install",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not start pnputil.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        return new DriverInstallResult(process.ExitCode == 0, output.Trim());
    }
}
