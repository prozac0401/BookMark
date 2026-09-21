param([string]$DotNetPath, [string]$NodePath, [switch]$SkipTests, [switch]$Package)
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
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- adapter-checks
        if ($LASTEXITCODE -ne 0) { throw 'Windows adapter regression checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- evidence-selftest
        if ($LASTEXITCODE -ne 0) { throw 'Excel series evidence checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- session-selftest
        if ($LASTEXITCODE -ne 0) { throw 'Manual capture session checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- word-checks
        if ($LASTEXITCODE -ne 0) { throw 'Word coordinate checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- office-url-checks
        if ($LASTEXITCODE -ne 0) { throw 'Office URL adapter checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- notepad-checks
        if ($LASTEXITCODE -ne 0) { throw 'Notepad adapter checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build -- pdf-checks
        if ($LASTEXITCODE -ne 0) { throw 'PDF transport checks failed.' }
        $browserHost = Join-Path $taskRoot 'src/WorkBookmark.BrowserHost/bin/Release/net10.0-windows/WorkBookmark.BrowserHost.exe'
        & $DotNetPath run --project tests/WorkBookmark.Browser.Tests -c Release --no-build -- $browserHost
        if ($LASTEXITCODE -ne 0) { throw 'Browser native messaging checks failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Desktop.Tests -c Release --no-build -- --browser-sqlite $browserHost
        if ($LASTEXITCODE -ne 0) { throw 'Browser/SQLite integration checks failed.' }
        if (-not $NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
        $browserTests = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'browser-extension/tests') -Filter '*.test.mjs' -File | Select-Object -ExpandProperty FullName
        & $NodePath --test @browserTests
        if ($LASTEXITCODE -ne 0) { throw 'Browser extension checks failed.' }
        & (Join-Path $taskRoot 'tools/WindowsChecks/BrowserRegistrationOwnershipChecks.ps1')
    }
    if ($Package) {
        $outputDirectory = Join-Path $taskRoot 'artifacts/publish/win-x64'
        & $DotNetPath publish src/WorkBookmark.App -c Release -r win-x64 --self-contained true --no-restore -o $outputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        & $DotNetPath publish src/WorkBookmark.BrowserHost -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $outputDirectory 'browser-host')
        if ($LASTEXITCODE -ne 0) { throw 'Browser host publish failed.' }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'browser-extension') -Destination $outputDirectory -Recurse -Force
        $setupDirectory = Join-Path $outputDirectory 'scripts'
        New-Item -ItemType Directory -Path $setupDirectory -Force | Out-Null
        foreach ($setupName in @('browser-registration-common.ps1', 'register-browser-host.ps1', 'unregister-browser-host.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $setupName) -Destination $setupDirectory -Force
        }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $outputDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $outputDirectory -Force
        & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory $outputDirectory
    }
}
finally { Pop-Location }
