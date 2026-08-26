$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appPublish = Join-Path $root 'artifacts\MUX.Virtual'
$driverProject = Join-Path $root 'driver\MUX.Virtual.Display\MUX.Virtual.Display.vcxproj'
$driverBuildRoot = Join-Path $root 'driver\MUX.Virtual.Display\bin\x64\Release'
$driverPublish = Join-Path $appPublish 'Driver'

Push-Location $root
try {
    Write-Host 'Validating MUX.Core...' -ForegroundColor Cyan
    dotnet run --project .\tests\MUX.Core.Tests\MUX.Core.Tests.csproj -c Release

    Write-Host 'Building MUX Virtual controller...' -ForegroundColor Cyan
    dotnet restore .\src\MUX.Virtual.App\MUX.Virtual.App.csproj
    dotnet build .\src\MUX.Virtual.App\MUX.Virtual.App.csproj -c Release --no-restore

    if (Test-Path $appPublish) { Remove-Item $appPublish -Recurse -Force }
    dotnet publish .\src\MUX.Virtual.App\MUX.Virtual.App.csproj `
        -c Release -r win-x64 --self-contained true -o $appPublish

    if (-not (Get-Command nuget -ErrorAction SilentlyContinue)) {
        throw 'nuget.exe is required to restore the Windows Driver Kit packages.'
    }

    Write-Host 'Restoring Windows Driver Kit packages...' -ForegroundColor Cyan
    nuget restore .\driver\packages.config -PackagesDirectory .\driver\packages

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Visual Studio locator could not be found.'
    }

    $wdkComponentIds = @('Microsoft.Windows.DriverKit', 'Component.Microsoft.Windows.DriverKit.BuildTools')
    $vsInstall = $null
    foreach ($componentId in $wdkComponentIds) {
        $candidate = & $vswhere -latest -products * -requires $componentId -property installationPath 2>$null |
            Select-Object -First 1
        if ($candidate) {
            $vsInstall = $candidate
            break
        }
    }

    if (-not $vsInstall) {
        throw 'Visual Studio with the Windows Driver Kit component could not be located.'
    }

    if (-not $env:VSCMD_VER) {
        $devShellDll = Join-Path $vsInstall 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
        if (-not (Test-Path $devShellDll)) {
            throw "Visual Studio Developer Shell module could not be found at $devShellDll."
        }

        Import-Module $devShellDll
        Enter-VsDevShell -VsInstallPath $vsInstall
        Set-Location $root
    }

    # WDK 28000 x64 builds must use 64-bit MSBuild. Using the 32-bit host makes
    # INF verification and PREfast attempt to load incompatible x86 host DLLs.
    $msbuild = Join-Path $vsInstall 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path $msbuild)) {
        throw "64-bit MSBuild could not be found at $msbuild."
    }

    $wdkPackages = Join-Path $root 'driver\packages'
    $requiredWdkTools = @('stampinf.exe', 'inf2cat.exe', 'signtool.exe', 'tracewpp.exe')
    $wdkToolDirs = @()

    foreach ($toolName in $requiredWdkTools) {
        $candidates = Get-ChildItem -Path $wdkPackages -Recurse -File -Filter $toolName -ErrorAction SilentlyContinue

        $tool = $candidates |
            Where-Object { $_.Directory.Name -in @('x64', 'amd64') } |
            Select-Object -First 1

        if (-not $tool) {
            $tool = $candidates |
                Where-Object { $_.Directory.Name -eq 'x86' } |
                Select-Object -First 1
        }

        if (-not $tool) {
            throw "No x64/amd64/x86 host copy of $toolName was found in the restored WDK/SDK packages."
        }

        $wdkToolDirs += $tool.DirectoryName
        Write-Host "Found WDK tool $toolName at $($tool.FullName)" -ForegroundColor DarkGray
    }

    $wdkToolDirs = $wdkToolDirs | Sort-Object -Unique
    $env:PATH = (($wdkToolDirs -join ';') + ';' + $env:PATH)

    $stampInf = Get-Command stampinf.exe -ErrorAction Stop
    if ($stampInf.Source -match 'ARM64') {
        throw "Refusing to execute ARM64 stampinf.exe on the x64 build host: $($stampInf.Source)"
    }

    Write-Host "Building MUX IddCx virtual display driver with: $msbuild" -ForegroundColor Cyan
    Write-Host "Using Visual Studio: $vsInstall" -ForegroundColor DarkGray
    Write-Host "Using StampInf: $($stampInf.Source)" -ForegroundColor DarkGray

    & $msbuild $driverProject /m /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    New-Item -ItemType Directory -Path $driverPublish -Force | Out-Null

    foreach ($name in @('MUXVirtualDisplay.dll', 'MUXVirtualDisplay.inf', 'MUXVirtualDisplay.cat')) {
        $candidate = Get-ChildItem $driverBuildRoot -Recurse -Filter $name |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $candidate) {
            throw "Driver build did not produce $name."
        }

        Copy-Item $candidate.FullName (Join-Path $driverPublish $name) -Force
    }

    $catPath = Join-Path $driverPublish 'MUXVirtualDisplay.cat'
    $signature = Get-AuthenticodeSignature -FilePath $catPath
    if (-not $signature.SignerCertificate) {
        throw 'The generated driver catalog does not expose a signing certificate.'
    }

    $certPath = Join-Path $driverPublish 'MUXVirtualDisplay-TestCertificate.cer'
    $certBytes = $signature.SignerCertificate.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($certPath, $certBytes)
    Write-Host "Exported the public driver test certificate: $certPath" -ForegroundColor DarkGray

    Copy-Item .\docs\VIRTUAL-DISPLAYS.md (Join-Path $appPublish 'VIRTUAL-DISPLAYS.md') -Force

    $zip = Join-Path $root 'artifacts\MUX-Virtual-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$appPublish\*" -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "Built: $zip" -ForegroundColor Green
}
finally {
    Pop-Location
}
