param(
    [Parameter(Mandatory=$true)][string]$MsiPath,
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [switch]$Extract
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
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
function ReadStreamHash([string]$table, [string]$name) {
    if ($table -notin @('Binary', 'Icon') -or $name -notmatch '^[A-Za-z0-9_]+$') { throw 'Invalid MSI asset identifier.' }
    $view = $database.OpenView(('SELECT `Data` FROM `{0}` WHERE `Name` = ''{1}''' -f $table, $name))
    $stream = [IO.MemoryStream]::new()
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "Missing MSI asset: $table/$name" }
        try {
            # msiReadStreamBytes returns a BSTR with one byte per character, including zero bytes.
            $byteEncoding = [Text.Encoding]::GetEncoding(28591)
            do {
                $chunk = [string]$record.ReadStream(1, 65536, 1)
                if ($chunk.Length -gt 0) {
                    $bytes = $byteEncoding.GetBytes($chunk)
                    $stream.Write($bytes, 0, $bytes.Length)
                }
            } while ($chunk.Length -gt 0)
            $stream.Position = 0
            return [pscustomobject]@{ Length = $stream.Length; Hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
    }
    finally {
        $sha.Dispose(); $stream.Dispose()
        [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
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
    Assert ($properties.ProductLanguage -eq '1033') 'MSI keeps the product language compatible with older releases'
    Assert (-not $properties.ContainsKey('LIMITUI')) 'Interactive install allows the full completion dialog'
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
    $shortcuts = ReadRows 'SELECT `Shortcut`, `Directory_`, `Target`, `Description`, `Icon_` FROM `Shortcut`'
    Assert (@($shortcuts | Where-Object { $_[1] -eq 'WorkBookmarkMenu' -and $_[2] -eq '[INSTALLFOLDER]WorkBookmark.exe' }).Count -eq 1) 'Start Menu shortcut launches installed application'
    Assert (@($shortcuts | Where-Object { $_[1] -eq 'StartupFolder' -and $_[2] -eq '[INSTALLFOLDER]WorkBookmark.exe' -and $_[3] -eq 'WorkBookmark per-user startup shortcut v1' }).Count -eq 1) 'Logon shortcut is recognized by application settings'
    Assert ($properties.ARPPRODUCTICON -eq 'WorkBookmarkIcon' -and @($shortcuts | Where-Object { $_[4] -ne 'WorkBookmarkIcon' }).Count -eq 0) 'Windows Apps and every application shortcut use the branded product icon'
    $productIcon = ReadStreamHash 'Icon' 'WorkBookmarkIcon'
    Assert ($productIcon.Hash -eq (Get-FileHash -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe') -Algorithm SHA256).Hash) 'Packaged product icon resource matches the published application executable'
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
    $installFiles = @($actions | Where-Object { $_[0] -eq 'InstallFiles' })
    Assert ($removeOld.Count -eq 1 -and [int]$removeOld[0][2] -gt [int]$initialize[0][2] -and [int]$removeOld[0][2] -lt [int]$installFiles[0][2]) 'Upgrade removes the old product before installing files within the rollback transaction'
    $upgradeRows = ReadRows 'SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`, `Remove`, `ActionProperty` FROM `Upgrade`'
    $olderProducts = @($upgradeRows | Where-Object { $_[6] -eq 'WIX_UPGRADE_DETECTED' })
    Assert ($olderProducts.Count -eq 1 -and $olderProducts[0][0] -eq $properties.UpgradeCode -and $olderProducts[0][2] -eq $expectedVersion -and ([int]$olderProducts[0][4] -band 2) -eq 0 -and ([int]$olderProducts[0][4] -band 512) -eq 0 -and -not $olderProducts[0][5]) 'Older MSI versions are detected for complete automatic removal'
    $newerProducts = @($upgradeRows | Where-Object { $_[6] -eq 'WIX_DOWNGRADE_DETECTED' })
    Assert ($newerProducts.Count -eq 1 -and $newerProducts[0][1] -eq $expectedVersion -and ([int]$newerProducts[0][4] -band 2) -ne 0) 'Newer versions are detected without removing them'
    $launchConditions = ReadRows 'SELECT `Condition`, `Description` FROM `LaunchCondition`'
    Assert (@($launchConditions | Where-Object { $_[0] -eq 'NOT WIX_DOWNGRADE_DETECTED' }).Count -eq 1) 'Downgrades are blocked by a launch condition'

    Assert ($properties.WIXUI_EXITDIALOGOPTIONALCHECKBOX -eq '1' -and $properties.WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT -eq '업무 책갈피 실행') 'Completion launch checkbox is labeled in Korean and checked by default'
    $controls = ReadRows 'SELECT `Dialog_`, `Control`, `Type`, `Property`, `Text` FROM `Control`'
    foreach ($artwork in @(@{ Name = 'WixUI_Bmp_Dialog'; File = 'dialog.bmp' }, @{ Name = 'WixUI_Bmp_Banner'; File = 'banner.bmp' })) {
        $compiledArtwork = ReadStreamHash 'Binary' $artwork.Name
        $sourceArtwork = Join-Path $taskRoot ('installer/Assets/' + $artwork.File)
        Assert ($compiledArtwork.Hash -eq (Get-FileHash -LiteralPath $sourceArtwork -Algorithm SHA256).Hash) ("Installer embeds the exact branded artwork: " + $artwork.File)
    }
    Assert (@($controls | Where-Object { $_[0] -in @('WelcomeDlg', 'ExitDialog') -and $_[2] -eq 'Bitmap' -and $_[4] -eq 'WixUI_Bmp_Dialog' }).Count -eq 2) 'Welcome and completion dialogs display the branded artwork'
    Assert (@($controls | Where-Object { $_[0] -eq 'ProgressDlg' -and $_[2] -eq 'Bitmap' -and $_[4] -eq 'WixUI_Bmp_Banner' }).Count -eq 1) 'Installation progress displays the branded banner'
    Assert (@($controls | Where-Object { $_[4] -match '시험판|체험판|제한된\s*(버전|시험판)|\btrial\b|\blimited\s+(version|edition)\b' }).Count -eq 0) 'Installer screens contain no trial or limited-edition wording'
    Assert (@($controls | Where-Object { $_[0] -eq 'ExitDialog' -and $_[1] -eq 'OptionalCheckBox' -and $_[2] -eq 'CheckBox' -and $_[3] -eq 'WIXUI_EXITDIALOGOPTIONALCHECKBOX' -and $_[4] -eq '[WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT]' }).Count -eq 1) 'Completion dialog contains the launch checkbox'
    $checkboxes = ReadRows 'SELECT `Property`, `Value` FROM `CheckBox`'
    Assert (@($checkboxes | Where-Object { $_[0] -eq 'WIXUI_EXITDIALOGOPTIONALCHECKBOX' -and $_[1] -eq '1' }).Count -eq 1) 'Checkbox selection controls the launch property'
    $controlConditions = ReadRows 'SELECT `Dialog_`, `Control_`, `Action`, `Condition` FROM `ControlCondition`'
    Assert (@($controlConditions | Where-Object { $_[0] -eq 'ExitDialog' -and $_[1] -eq 'OptionalCheckBox' -and $_[2] -eq 'Show' -and $_[3] -eq 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT AND NOT Installed' }).Count -eq 1) 'Launch checkbox is shown for first installation and upgrades, not maintenance'
    $uiActions = ReadRows 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallUISequence`'
    Assert (@($uiActions | Where-Object { $_[0] -eq 'ExitDialog' -and $_[2] -eq '-1' }).Count -eq 1) 'Completion dialog is scheduled only after successful installation'
    $events = ReadRows 'SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering` FROM `ControlEvent`'
    $launchEvents = @($events | Where-Object { $_[2] -eq 'DoAction' -and $_[3] -eq 'LaunchWorkBookmarkAfterInstall' })
    $launchCondition = 'WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND NOT Installed AND NOT REMOVE AND NOT REINSTALL AND NOT UPGRADINGPRODUCTCODE AND ACTION = "INSTALL" AND UILevel = 5'
    Assert ($launchEvents.Count -eq 1 -and $launchEvents[0][0] -eq 'ExitDialog' -and $launchEvents[0][1] -eq 'Finish' -and $launchEvents[0][4] -eq $launchCondition) 'Only checked Finish on successful interactive install or upgrade can launch the application'
    $finish = @($events | Where-Object { $_[0] -eq 'ExitDialog' -and $_[1] -eq 'Finish' -and $_[2] -eq 'EndDialog' -and $_[3] -eq 'Return' })
    Assert ($finish.Count -eq 1 -and [int]$finish[0][5] -gt [int]$launchEvents[0][5]) 'Finish closes the wizard after processing the optional launch'
    Assert (@($actions | Where-Object { $_[0] -eq 'LaunchWorkBookmarkAfterInstall' }).Count -eq 0 -and @($uiActions | Where-Object { $_[0] -eq 'LaunchWorkBookmarkAfterInstall' }).Count -eq 0) 'Silent, basic UI, repair, and uninstall sequences never launch the application'
    $customActions = ReadRows 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`'
    $launchAction = @($customActions | Where-Object { $_[0] -eq 'LaunchWorkBookmarkAfterInstall' })
    Assert ($launchAction.Count -eq 1 -and $launchAction[0][2] -eq 'Wix4UtilCA_X64' -and $launchAction[0][3] -eq 'WixUnelevatedShellExec' -and ([int]$launchAction[0][1] -band 3072) -eq 0 -and $properties.WixUnelevatedShellExecTarget -eq '[#WorkBookmarkExecutable]') 'Finish launches the installed executable through the unelevated user shell'

    # OpenPackage option 1 creates a restricted, machine-state-independent session. No install actions run.
    # Evaluate the actual compiled MSI expression with Windows Installer instead of a mock expression parser.
    $conditionSession = $installer.OpenPackage($msiPath, 1)
    try {
        $scenarios = @(
            @{ Name = 'checked fresh install'; Values = @{}; Expected = 1 },
            @{ Name = 'checked upgrade'; Values = @{ WIX_UPGRADE_DETECTED = '{A809F096-5045-076F-33A2-EDCFC2189C28}' }; Expected = 1 },
            @{ Name = 'unchecked install'; Values = @{ WIXUI_EXITDIALOGOPTIONALCHECKBOX = '' }; Expected = 0 },
            @{ Name = 'unchecked upgrade'; Values = @{ WIX_UPGRADE_DETECTED = '{A809F096-5045-076F-33A2-EDCFC2189C28}'; WIXUI_EXITDIALOGOPTIONALCHECKBOX = '' }; Expected = 0 },
            @{ Name = 'silent install'; Values = @{ UILevel = '2' }; Expected = 0 },
            @{ Name = 'basic UI install'; Values = @{ UILevel = '3' }; Expected = 0 },
            @{ Name = 'reduced UI install'; Values = @{ UILevel = '4' }; Expected = 0 },
            @{ Name = 'repair'; Values = @{ Installed = '1'; REINSTALL = 'ALL' }; Expected = 0 },
            @{ Name = 'uninstall'; Values = @{ Installed = '1'; REMOVE = 'ALL' }; Expected = 0 },
            @{ Name = 'old product removal during upgrade'; Values = @{ UPGRADINGPRODUCTCODE = '{00000000-0000-0000-0000-000000000001}' }; Expected = 0 },
            @{ Name = 'administrative extraction'; Values = @{ ACTION = 'ADMIN' }; Expected = 0 }
        )
        foreach ($scenario in $scenarios) {
            $scenarioProperties = @{
                WIXUI_EXITDIALOGOPTIONALCHECKBOX = '1'; Installed = ''; REMOVE = ''; REINSTALL = ''
                UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = ''; ACTION = 'INSTALL'; UILevel = '5'
            }
            foreach ($key in $scenario.Values.Keys) { $scenarioProperties[$key] = $scenario.Values[$key] }
            foreach ($key in $scenarioProperties.Keys) { $conditionSession.Property($key) = $scenarioProperties[$key] }
            Assert ($conditionSession.EvaluateCondition($launchEvents[0][4]) -eq $scenario.Expected) ("Windows Installer launch condition: " + $scenario.Name)
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($conditionSession) }
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

if ($Extract) {
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
