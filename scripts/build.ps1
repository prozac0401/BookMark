param([string]$DotNetPath, [switch]$SkipTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotNetPath) {
    $DotNetPath = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotNetPath)) { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotNetPath = (Resolve-Path -LiteralPath $DotNetPath).Path
$env:DOTNET_ROOT = Split-Path -Parent $DotNetPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $taskRoot
try {
    & $DotNetPath restore WorkBookmark.sln --locked-mode --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotNetPath build WorkBookmark.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $DotNetPath run --project tests/WorkBookmark.Core.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Core/Storage tests failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Integration.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'IPC tests failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Desktop.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Desktop component tests failed.' }
    }
    if ($Package) {
        $outputDirectory = Join-Path $taskRoot 'artifacts/publish/win-x64'
        & $DotNetPath publish src/WorkBookmark.App -c Release -r win-x64 --self-contained true --no-restore -o $outputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $outputDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $outputDirectory -Force
        & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory $outputDirectory
    }
}
finally { Pop-Location }
