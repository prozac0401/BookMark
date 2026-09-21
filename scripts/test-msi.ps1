param(
    [Parameter(Mandatory=$true)][string]$MsiPath,
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [switch]$Extract
)
$ErrorActionPreference = 'Stop'
$msiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$checks = [Collections.Generic.List[object]]::new()
function Assert([bool]$condition, [string]$name) {
    $checks.Add([pscustomobject]@{ name = $name; passed = $condition })
    if (-not $condition) { throw "FAIL: $name" }
    Write-Host "PASS: $name"
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msiPath, 0)
function ReadRows([string]$query) {
    $view = $database.OpenView($query)
    try {
        [void]$view.Execute()
        $rows = [Collections.Generic.List[object]]::new()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $values = @(); for ($index = 1; $index -le $record.FieldCount(); $index++) { $values += [string]$record.StringData($index) }
                $rows.Add($values)
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
        return ,$rows
    }
    finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
try {
    $properties = @{}
    foreach ($row in (ReadRows 'SELECT `Property`, `Value` FROM `Property`')) { $properties[$row[0]] = $row[1] }
    $expectedVersion = ((Get-Item -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe')).VersionInfo.ProductVersion -split '\+')[0]
    Assert ($properties.ProductName -eq 'WorkBookmark' -and $properties.ProductVersion -eq $expectedVersion) 'MSI product/version match published executable'
    Assert (-not $properties.ContainsKey('ALLUSERS')) 'MSI installs per user'
    $summary = $database.SummaryInformation(0)
    try {
        Assert (($summary.Property(7) -split ';')[0] -eq 'x64') 'MSI targets x64'
        Assert (([int]$summary.Property(15) -band 8) -ne 0) 'MSI does not require elevation'
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    Assert ($properties.UpgradeCode -eq '{BB5C5CA8-5BA0-4B01-875E-9E295690C711}') 'MSI uses the stable upgrade family'
    Assert ($properties.ARPNOMODIFY -eq '1' -and -not $properties.ContainsKey('ARPSYSTEMCOMPONENT') -and -not $properties.ContainsKey('ARPNOREMOVE')) 'MSI is removable in Windows Apps / Control Panel'
    $directories = @{}
    foreach ($row in (ReadRows 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`')) { $directories[$row[0]] = $row }
    Assert ($directories.INSTALLFOLDER[1] -eq 'ProgramsFolder' -and $directories.ProgramsFolder[1] -eq 'LocalAppDataFolder') 'Application lives under LocalAppData Programs'
    $components = ReadRows 'SELECT `Component`, `ComponentId`, `Directory_`, `Attributes`, `Condition`, `KeyPath` FROM `Component`'
    Assert (@($components | Where-Object { -not $_[1] }).Count -eq 0 -and @($components | ForEach-Object { $_[1] } | Select-Object -Unique).Count -eq $components.Count) 'Components have unique stable identities'
    Assert (@($components | Where-Object { ([int]$_[3] -band 4) -eq 0 }).Count -eq 0) 'Per-user components use registry key paths'
    $startupComponent = @($components | Where-Object { $_[0] -eq 'StartupShortcut' })
    Assert ($startupComponent.Count -eq 1 -and $startupComponent[0][4] -eq 'STARTUP_PREFERENCE <> "#0"') 'Startup default respects persisted opt-out on install and upgrade'
    $registry = ReadRows 'SELECT `Root`, `Key`, `Name` FROM `Registry`'
    Assert (@($registry | Where-Object { $_[0] -ne '1' -or -not $_[1].StartsWith('Software\WorkBookmark\Installer') }).Count -eq 0) 'Installer owns only its HKCU registration keys'
    $shortcuts = ReadRows 'SELECT `Shortcut`, `Directory_`, `Target`, `Description` FROM `Shortcut`'
    Assert (@($shortcuts | Where-Object { $_[1] -eq 'WorkBookmarkMenu' -and $_[2] -eq '[INSTALLFOLDER]WorkBookmark.exe' }).Count -eq 1) 'Start Menu shortcut launches installed application'
    Assert (@($shortcuts | Where-Object { $_[1] -eq 'StartupFolder' -and $_[2] -eq '[INSTALLFOLDER]WorkBookmark.exe' -and $_[3] -eq 'WorkBookmark per-user startup shortcut v1' }).Count -eq 1) 'Logon shortcut is recognized by application settings'
    $removals = ReadRows 'SELECT `FileName`, `DirProperty`, `InstallMode` FROM `RemoveFile`'
    Assert (@($removals | Where-Object { ($_[0] -split '\|')[-1] -eq 'WorkBookmark.lnk' -and $_[1] -eq 'StartupFolder' -and $_[2] -eq '2' }).Count -eq 1) 'Uninstall removes startup shortcut even when recreated by application'
    Assert (@($removals | Where-Object { $_[0] -match '[*?]' -or $_[1] -eq 'LocalAppDataFolder' }).Count -eq 0) 'Uninstall has no wildcard or user-data cleanup'
    $files = ReadRows 'SELECT `File`, `FileName`, `FileSize` FROM `File`'
    $published = @(Get-ChildItem -LiteralPath $publishPath -Recurse -Force -File)
    Assert ($files.Count -eq $published.Count -and @($files | Where-Object { $_[1] -match '(^|\|)(bookmarks\.db|settings\.json)$' }).Count -eq 0) 'Payload count matches publish and does not package user data'
    $actions = ReadRows 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`'
    $stop = @($actions | Where-Object { $_[0] -eq 'StopWorkBookmark' })
    $validate = @($actions | Where-Object { $_[0] -eq 'InstallValidate' })
    Assert ($stop.Count -eq 1 -and [int]$stop[0][2] -lt [int]$validate[0][2] -and $stop[0][1] -eq 'Installed OR WIX_UPGRADE_DETECTED') 'Upgrade/uninstall request graceful shutdown before file validation'
    $removeOld = @($actions | Where-Object { $_[0] -eq 'RemoveExistingProducts' })
    $initialize = @($actions | Where-Object { $_[0] -eq 'InstallInitialize' })
    Assert ($removeOld.Count -eq 1 -and [int]$removeOld[0][2] -gt [int]$initialize[0][2]) 'Upgrade removal occurs inside the rollback transaction'
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

if ($Extract) {
    $taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
    $extractionPath = Join-Path $taskRoot ('artifacts/installer-extraction/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $extractionPath -Force | Out-Null
    $logPath = Join-Path $extractionPath 'administrative-extraction.log'
    # /a creates an administrative image only: it neither installs this product nor runs install custom actions.
    $arguments = '/a "' + $msiPath + '" /qn TARGETDIR="' + $extractionPath + '" /L*v "' + $logPath + '"'
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Administrative extraction did not finish within 60 seconds. See $logPath" }
    Assert ($process.ExitCode -eq 0) 'MSI administrative extraction succeeds without installing product'
    $executable = @(Get-ChildItem -LiteralPath $extractionPath -Filter WorkBookmark.exe -Recurse -File)
    Assert ($executable.Count -eq 1) 'Administrative image contains one application executable'
    $extractedPayload = $executable[0].DirectoryName
    $extractedFiles = @(Get-ChildItem -LiteralPath $extractedPayload -Recurse -File)
    Assert ($extractedFiles.Count -eq $published.Count) 'Extracted payload has the complete published file set'
    $mismatches = @()
    foreach ($file in $published) {
        $relative = $file.FullName.Substring($publishPath.Length + 1)
        $extracted = Join-Path $extractedPayload $relative
        if (-not (Test-Path -LiteralPath $extracted) -or (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { $mismatches += $relative }
    }
    Assert ($mismatches.Count -eq 0) 'Every extracted file SHA-256 matches the publish directory'
}
$report = [ordered]@{
    msi = [IO.Path]::GetFileName($msiPath)
    sha256 = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    productVersion = $expectedVersion
    automaticChecks = $checks.Count
    passed = @($checks | Where-Object passed).Count
    administrativeExtraction = [bool]$Extract
    liveInstallUninstallReboot = 'NOT RUN: verify in a disposable Windows user account or virtual machine.'
    checks = $checks
}
$reportPath = [IO.Path]::ChangeExtension($msiPath, '.validation.json')
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Output $reportPath
