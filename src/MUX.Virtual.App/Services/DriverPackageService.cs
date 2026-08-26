namespace MUX.Virtual.App.Services;

public sealed record DriverInstallResult(
    bool Success,
    string Output,
    bool TrustRequired = false);

public sealed class DriverPackageService
{
    private const string TestCertificateFileName =
        "MUXVirtualDisplay-TestCertificate.cer";

    public string DriverDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Driver");

    public string InfPath =>
        Path.Combine(DriverDirectory, "MUXVirtualDisplay.inf");

    public string CatalogPath =>
        Path.Combine(DriverDirectory, "MUXVirtualDisplay.cat");

    public string TestCertificatePath =>
        Path.Combine(DriverDirectory, TestCertificateFileName);

    public bool PackageIsBundled =>
        File.Exists(InfPath) &&
        File.Exists(Path.Combine(DriverDirectory, "MUXVirtualDisplay.dll")) &&
        File.Exists(CatalogPath);

    public bool TestCertificateIsBundled =>
        File.Exists(TestCertificatePath);

    public async Task<DriverInstallResult> InstallAsync(
        bool allowTestCertificateTrust = false)
    {
        if (!PackageIsBundled)
        {
            return new DriverInstallResult(
                false,
                "The MUX Virtual display driver package is incomplete. " +
                "Download and extract the current MUX Virtual release ZIP, " +
                "which must contain the complete Driver folder.");
        }

        var firstAttempt = await RunPnPUtilAsync();
        if (firstAttempt.Success)
        {
            return new DriverInstallResult(
                true,
                "MUX Virtual display driver installed successfully.\n\n" +
                firstAttempt.Output);
        }

        if (!LooksLikeUntrustedRoot(firstAttempt.Output))
        {
            return new DriverInstallResult(false, firstAttempt.Output);
        }

        if (!TestCertificateIsBundled)
        {
            return new DriverInstallResult(
                false,
                "Windows rejected the development driver because its test " +
                "certificate is not trusted, and this package does not contain " +
                $"{TestCertificateFileName}. Download the newest MUX Virtual ZIP.");
        }

        if (!allowTestCertificateTrust)
        {
            return new DriverInstallResult(
                false,
                "This MUX Virtual build uses a development/test-signed driver. " +
                "Windows must trust the included public build certificate before " +
                "the driver package can be staged. MUX can add that certificate " +
                "to the Local Computer Trusted Root and Trusted Publishers stores, " +
                "then retry the installation.",
                TrustRequired: true);
        }

        var trustResult = await TrustTestCertificateAsync();
        if (!trustResult.Success)
        {
            return new DriverInstallResult(
                false,
                "MUX could not trust the included development driver certificate.\n\n" +
                trustResult.Output);
        }

        var retry = await RunPnPUtilAsync();
        if (retry.Success)
        {
            return new DriverInstallResult(
                true,
                "MUX trusted the development signing certificate and installed " +
                "the virtual display driver successfully.\n\n" + retry.Output);
        }

        return new DriverInstallResult(
            false,
            "The included development certificate is now trusted, but Windows " +
            "still refused the test-signed driver. This machine may require " +
            "Windows Test Mode for development drivers. MUX does not change " +
            "Secure Boot or boot configuration automatically. A normal retail " +
            "installation requires the driver package to be signed by Microsoft " +
            "through the Windows Hardware/Partner Center.\n\n" + retry.Output);
    }

    private async Task<ProcessResult> RunPnPUtilAsync()
    {
        return await RunProcessAsync(
            SystemTool("pnputil.exe"),
            $"/add-driver \"{InfPath}\" /install");
    }

    private async Task<ProcessResult> TrustTestCertificateAsync()
    {
        var root = await RunProcessAsync(
            SystemTool("certutil.exe"),
            $"-addstore -f Root \"{TestCertificatePath}\"");

        if (!root.Success)
        {
            return root;
        }

        var publisher = await RunProcessAsync(
            SystemTool("certutil.exe"),
            $"-addstore -f TrustedPublisher \"{TestCertificatePath}\"");

        var combined =
            "Trusted Root:\n" + root.Output +
            "\n\nTrusted Publishers:\n" + publisher.Output;

        return new ProcessResult(
            publisher.Success,
            publisher.ExitCode,
            combined);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"Windows could not start {Path.GetFileName(fileName)}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        return new ProcessResult(
            process.ExitCode == 0,
            process.ExitCode,
            output);
    }

    private static string SystemTool(string name) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            name);

    private static bool LooksLikeUntrustedRoot(string output)
    {
        return output.Contains(
                   "root certificate which is not trusted",
                   StringComparison.OrdinalIgnoreCase) ||
               output.Contains(
                   "not trusted by the trust provider",
                   StringComparison.OrdinalIgnoreCase) ||
               output.Contains(
                   "CERT_E_UNTRUSTEDROOT",
                   StringComparison.OrdinalIgnoreCase) ||
               output.Contains(
                   "0x800B0109",
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProcessResult(
        bool Success,
        int ExitCode,
        string Output);
}
