namespace MUX.Virtual.App.Services;

public sealed record DriverInstallResult(
    bool Success,
    string Output,
    bool TrustRequired = false);

public sealed record DriverRuntimeStatus(
    bool PackagePresent,
    bool DevelopmentBuild,
    bool TestSigningEnabled,
    string Summary);

public sealed class DriverPackageService
{
    private const string TestCertificateFileName = "MUXVirtualDisplay-TestCertificate.cer";

    public string DriverDirectory => Path.Combine(AppContext.BaseDirectory, "Driver");
    public string InfPath => Path.Combine(DriverDirectory, "MUXVirtualDisplay.inf");
    public string CatalogPath => Path.Combine(DriverDirectory, "MUXVirtualDisplay.cat");
    public string DriverDllPath => Path.Combine(DriverDirectory, "MUXVirtualDisplay.dll");
    public string TestCertificatePath => Path.Combine(DriverDirectory, TestCertificateFileName);

    public bool PackageIsBundled =>
        File.Exists(InfPath) && File.Exists(DriverDllPath) && File.Exists(CatalogPath);

    public bool TestCertificateIsBundled => File.Exists(TestCertificatePath);

    public DriverRuntimeStatus GetRuntimeStatus()
    {
        if (!PackageIsBundled)
        {
            return new DriverRuntimeStatus(false, false, false,
                "Driver package is missing. Download and fully extract the MUX Virtual ZIP.");
        }

        if (!TestCertificateIsBundled)
        {
            return new DriverRuntimeStatus(true, false, true,
                "Production driver package bundled · activation will verify/install it automatically.");
        }

        var testSigning = IsCurrentBootTestSigned();
        return new DriverRuntimeStatus(
            true,
            true,
            testSigning,
            testSigning
                ? "Development driver package bundled · Windows Test Mode is active. Activation will install/verify the driver automatically."
                : "Development driver package bundled · Windows Test Mode must be enabled and Windows restarted before activation.");
    }

    public async Task<bool> IsDriverStagedAsync()
    {
        try
        {
            var result = await RunProcessAsync(SystemTool("pnputil.exe"), "/enum-drivers");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                return false;
            }

            return result.Output.Contains("MUXVirtualDisplay.inf", StringComparison.OrdinalIgnoreCase) ||
                   result.Output.Contains("Triple Axis Capital", StringComparison.OrdinalIgnoreCase) &&
                   result.Output.Contains("Display", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<DriverInstallResult> InstallAsync(bool allowTestCertificateTrust = false)
    {
        if (!PackageIsBundled)
        {
            return new DriverInstallResult(false,
                "The MUX Virtual display driver package is incomplete. Download and fully extract the current MUX Virtual release ZIP.");
        }

        var firstAttempt = await RunPnPUtilAsync();
        if (firstAttempt.Success)
        {
            if (!await IsDriverStagedAsync())
            {
                return new DriverInstallResult(false,
                    "Windows reported that the MUX driver package was added, but MUX could not verify it in the Windows driver store.\n\n" + firstAttempt.Output);
            }

            return new DriverInstallResult(true,
                "MUX Virtual display driver staged and verified successfully.\n\n" + firstAttempt.Output);
        }

        if (!LooksLikeUntrustedRoot(firstAttempt.Output))
        {
            return new DriverInstallResult(false, firstAttempt.Output);
        }

        if (!TestCertificateIsBundled)
        {
            return new DriverInstallResult(false,
                "Windows rejected the driver signature and the package does not contain its development certificate. Download the newest MUX Virtual ZIP.");
        }

        if (!allowTestCertificateTrust)
        {
            return new DriverInstallResult(false,
                "This rolling MUX Virtual build is development/test-signed. Windows must trust the included public build certificate before the driver can be staged. MUX can add only that public certificate to the Local Computer Trusted Root and Trusted Publishers stores, then retry installation.",
                TrustRequired: true);
        }

        var trustResult = await TrustTestCertificateAsync();
        if (!trustResult.Success)
        {
            return new DriverInstallResult(false,
                "MUX could not trust the included development driver certificate.\n\n" + trustResult.Output);
        }

        var retry = await RunPnPUtilAsync();
        if (retry.Success)
        {
            if (!await IsDriverStagedAsync())
            {
                return new DriverInstallResult(false,
                    "The certificate is trusted and Windows reported that the driver was added, but MUX could not verify the package in the driver store.\n\n" + retry.Output);
            }

            return new DriverInstallResult(true,
                "MUX trusted the development signing certificate and staged/verified the virtual display driver successfully.\n\n" + retry.Output);
        }

        return new DriverInstallResult(false,
            "The development certificate is trusted, but Windows still refused the driver package.\n\n" + retry.Output);
    }

    public async Task<DriverInstallResult> EnableDevelopmentModeAsync()
    {
        if (!TestCertificateIsBundled)
        {
            return new DriverInstallResult(true,
                "This package does not require MUX development driver mode.");
        }

        if (IsCurrentBootTestSigned())
        {
            return new DriverInstallResult(true,
                "Windows Test Mode is already active for this boot.");
        }

        var result = await RunProcessAsync(
            SystemTool("bcdedit.exe"),
            "/set testsigning on");

        if (!result.Success)
        {
            var secureBootHint = result.Output.Contains("Secure Boot", StringComparison.OrdinalIgnoreCase)
                ? "\n\nSecure Boot is preventing Windows from enabling Test Mode. MUX will not disable Secure Boot automatically. For normal distribution, use a Microsoft production-signed driver."
                : string.Empty;

            return new DriverInstallResult(false,
                "Windows could not enable Test Mode.\n\n" + result.Output + secureBootHint);
        }

        return new DriverInstallResult(true,
            "Windows Test Mode has been enabled in the boot configuration. Restart Windows before activating MUX Virtual. This setting is only needed for the development/test-signed rolling build.\n\n" + result.Output);
    }

    public async Task<string> GetDeviceDiagnosticsAsync()
    {
        try
        {
            var result = await RunProcessAsync(
                SystemTool("pnputil.exe"),
                "/enum-devices /problem /deviceids");

            if (string.IsNullOrWhiteSpace(result.Output))
            {
                return string.Empty;
            }

            var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("MUXVirtualDisplay", StringComparison.OrdinalIgnoreCase) &&
                    !lines[i].Contains("MUX Virtual Display", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var start = Math.Max(0, i - 5);
                var end = Math.Min(lines.Length - 1, i + 5);
                for (var j = start; j <= end; j++)
                {
                    if (!matches.Contains(lines[j], StringComparer.OrdinalIgnoreCase))
                    {
                        matches.Add(lines[j]);
                    }
                }
            }

            return matches.Count == 0 ? string.Empty : string.Join(Environment.NewLine, matches);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<ProcessResult> RunPnPUtilAsync() =>
        await RunProcessAsync(SystemTool("pnputil.exe"), $"/add-driver \"{InfPath}\" /install");

    private async Task<ProcessResult> TrustTestCertificateAsync()
    {
        var root = await RunProcessAsync(SystemTool("certutil.exe"), $"-addstore -f Root \"{TestCertificatePath}\"");
        if (!root.Success)
        {
            return root;
        }

        var publisher = await RunProcessAsync(SystemTool("certutil.exe"), $"-addstore -f TrustedPublisher \"{TestCertificatePath}\"");
        return new ProcessResult(
            publisher.Success,
            publisher.ExitCode,
            "Trusted Root:\n" + root.Output + "\n\nTrusted Publishers:\n" + publisher.Output);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
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
            ?? throw new InvalidOperationException($"Windows could not start {Path.GetFileName(fileName)}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = string.Join(Environment.NewLine,
            new[] { (await stdoutTask).Trim(), (await stderrTask).Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        return new ProcessResult(process.ExitCode == 0, process.ExitCode, output);
    }

    private static string SystemTool(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", name);

    private static bool IsCurrentBootTestSigned()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control", writable: false);
            var options = key?.GetValue("SystemStartOptions") as string;
            return options?.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeUntrustedRoot(string output) =>
        output.Contains("root certificate which is not trusted", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("not trusted by the trust provider", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("CERT_E_UNTRUSTEDROOT", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("0x800B0109", StringComparison.OrdinalIgnoreCase);

    private sealed record ProcessResult(bool Success, int ExitCode, string Output);
}
