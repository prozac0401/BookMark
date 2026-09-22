param([string]$DotNetPath, [string]$NodePath, [switch]$SkipTests, [switch]$Package, [switch]$Msi, [string]$ArtifactsPath,
      [ValidateSet('Commit', 'WorkingTree')][string]$SourceMode = 'Commit')
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
$artifactArguments = @()
if ($ArtifactsPath) {
    # Use the SDK's isolated obj/bin layout consistently for restore, build, run and publish.
    $ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
    $artifactArguments = @('--artifacts-path', $ArtifactsPath)
}
Push-Location $taskRoot
try {
    & $DotNetPath restore WorkBookmark.sln --locked-mode --configfile NuGet.Config @artifactArguments
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotNetPath build WorkBookmark.sln -c Release --no-restore @artifactArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $DotNetPath run --project tests/WorkBookmark.Core.Tests -c Release --no-build @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'Core/Storage tests failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Integration.Tests -c Release --no-build @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'IPC tests failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Desktop.Tests -c Release --no-build @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'Desktop component tests failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- adapter-checks
        if ($LASTEXITCODE -ne 0) { throw 'Windows adapter regression checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- evidence-selftest
        if ($LASTEXITCODE -ne 0) { throw 'Excel series evidence checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- session-selftest
        if ($LASTEXITCODE -ne 0) { throw 'Manual capture session checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- word-checks
        if ($LASTEXITCODE -ne 0) { throw 'Word coordinate checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- office-url-checks
        if ($LASTEXITCODE -ne 0) { throw 'Office URL adapter checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- browser-checks
        if ($LASTEXITCODE -ne 0) { throw 'Browser accessibility checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- notepad-checks
        if ($LASTEXITCODE -ne 0) { throw 'Notepad adapter checks failed.' }
        & $DotNetPath run --project tools/WindowsChecks -c Release --no-build @artifactArguments -- pdf-checks
        if ($LASTEXITCODE -ne 0) { throw 'PDF transport checks failed.' }
        $browserHost = Join-Path $taskRoot 'src/WorkBookmark.BrowserHost/bin/Release/net10.0-windows/WorkBookmark.BrowserHost.exe'
        if ($ArtifactsPath) { $browserHost = Join-Path $ArtifactsPath 'bin/WorkBookmark.BrowserHost/release/WorkBookmark.BrowserHost.exe' }
        & $DotNetPath run --project tests/WorkBookmark.Browser.Tests -c Release --no-build @artifactArguments -- $browserHost
        if ($LASTEXITCODE -ne 0) { throw 'Browser native messaging checks failed.' }
        & $DotNetPath run --project tests/WorkBookmark.Desktop.Tests -c Release --no-build @artifactArguments -- --browser-sqlite $browserHost
        if ($LASTEXITCODE -ne 0) { throw 'Browser/SQLite integration checks failed.' }
        if (-not $NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
        $browserTests = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'browser-extension/tests') -Filter '*.test.mjs' -File | Select-Object -ExpandProperty FullName
        & $NodePath --test @browserTests
        if ($LASTEXITCODE -ne 0) { throw 'Browser extension checks failed.' }
        & (Join-Path $taskRoot 'tools/WindowsChecks/BrowserRegistrationOwnershipChecks.ps1')
    }
    if ($Package -or $Msi) {
        # A new directory prevents removed runtime/content files from leaking into the next release.
        $outputDirectory = Join-Path $taskRoot ('artifacts/publish/win-x64-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
        & $DotNetPath publish src/WorkBookmark.App -c Release -r win-x64 --self-contained true --no-restore -o $outputDirectory @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        & $DotNetPath publish src/WorkBookmark.BrowserHost -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $outputDirectory 'browser-host') @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'Browser host publish failed.' }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'browser-extension') -Destination $outputDirectory -Recurse -Force
        $setupDirectory = Join-Path $outputDirectory 'scripts'
        New-Item -ItemType Directory -Path $setupDirectory -Force | Out-Null
        foreach ($setupName in @('browser-registration-common.ps1', 'register-browser-host.ps1', 'unregister-browser-host.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $setupName) -Destination $setupDirectory -Force
        }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $outputDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $outputDirectory -Force
        $msiPath = $null
        if ($Msi) { $msiPath = & (Join-Path $PSScriptRoot 'build-msi.ps1') -PublishDirectory $outputDirectory -DotNetPath $DotNetPath }
        if ($Package) { & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory $outputDirectory -MsiPath $msiPath -SourceMode $SourceMode }
        elseif ($msiPath) { Write-Output $msiPath }
    }
}
finally { Pop-Location }
