[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$MsiPath,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][string]$PreviousMsiPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$local = [Environment]::GetFolderPath('LocalApplicationData')
$default = Join-Path $local 'Programs/WorkBookmark'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'WorkBookmark'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'WorkBookmark.lnk'
$key = 'HKCU:\Software\WorkBookmark\Installer'
$preferences = 'HKCU:\Software\WorkBookmark\Preferences'
$family = '{BB5C5CA8-5BA0-4B01-875E-9E295690C711}'
function Products {
    $api = New-Object -ComObject WindowsInstaller.Installer
    try { foreach ($code in $api.RelatedProducts($family)) { [pscustomobject]@{code=$code;version=$api.ProductInfo($code,'VersionString')} } }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($api) }
}
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use an ordinary user.' }
if (@(Products).Count -or (Test-Path $key) -or (Test-Path $default) -or (Test-Path $menu) -or (Test-Path $startup) -or @(Get-Process WorkBookmark -ErrorAction SilentlyContinue).Count) { throw 'Existing installation, shortcut or process: stop before making changes.' }
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$previous = (Resolve-Path -LiteralPath $PreviousMsiPath).Path
if ((Get-FileHash -LiteralPath $msi).Hash -ne $ExpectedSha256 -or $ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Candidate hash mismatch.' }
if ((Get-FileHash -LiteralPath $previous).Hash -ne '3424a44367e7e7c1e1483d359c595dd568d2368cea48b61e83e6b63c65eca714') { throw 'Expected exact public 0.2.3 MSI.' }
$root = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts/install-location-'+[guid]::NewGuid().ToString('N'))))
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence root outside repository artifacts.' }
New-Item -ItemType Directory -Path $root | Out-Null
$custom = Join-Path $root 'custom install'
$checks = [Collections.Generic.List[object]]::new()
$installed = $null
$activePath = $null
$status = 'running'
$failure = $null
function Check([string]$name,[bool]$ok) {
    $checks.Add([pscustomobject]@{name=$name;passed=$ok})
    if (-not $ok) { throw "FAIL: $name" }
    Write-Output "PASS: $name"
}
function Msi([string]$name,[string[]]$arguments) {
    $log = Join-Path $root ($name+'.log')
    $p = Start-Process msiexec.exe -ArgumentList ($arguments+@('/qn','/norestart','/l*v',('"'+$log+'"'))) -WindowStyle Hidden -PassThru -Wait
    Check ($name+' exit 0') ($p.ExitCode -eq 0)
}
function Snapshot-Data {
    $snapshot = [ordered]@{}
    foreach ($name in @('bookmarks.db','bookmarks.db-wal','bookmarks.db-shm','settings.json','stickers.json','sticker-layout.json')) {
        $path = Join-Path (Join-Path $local 'WorkBookmark') $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $snapshot[$name] = (Get-FileHash -LiteralPath $path).Hash }
    }
    return ($snapshot | ConvertTo-Json -Compress)
}
function Preference {
    if (-not (Test-Path $preferences)) { return '' }
    return ((Get-ItemProperty -LiteralPath $preferences | Select-Object * -ExcludeProperty PSPath,PSParentPath,PSChildName,PSDrive,PSProvider) | ConvertTo-Json -Compress)
}
$before = Snapshot-Data
$preferenceBefore = Preference
function Preserved([string]$stage) {
    Check ($stage+' user data hashes and inventory unchanged') ((Snapshot-Data) -eq $before)
    Check ($stage+' startup preference unchanged') ((Preference) -eq $preferenceBefore)
    Check ($stage+' no automatic application launch') (@(Get-Process WorkBookmark -ErrorAction SilentlyContinue).Count -eq 0)
}
function InstalledAt([string]$path,[string]$stage,[string]$productVersion='0.2.4') {
    $exe = Join-Path $path 'WorkBookmark.exe'
    Check ($stage+' candidate executable at expected location') ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -like '0.2.4*')
    Check ($stage+' saved location matches') ([IO.Path]::GetFullPath((Get-ItemPropertyValue $key 'InstallFolder')).TrimEnd('\') -eq [IO.Path]::GetFullPath($path).TrimEnd('\'))
    $products = @(Products)
    Check ($stage+' exactly one '+$productVersion+' product') ($products.Count -eq 1 -and $products[0].version -eq $productVersion)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut((Join-Path $menu 'WorkBookmark.lnk'))
        try { Check ($stage+' start menu target matches') ($link.TargetPath -eq $exe) }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    Preserved $stage
}
function Removed([string]$path,[string]$stage) {
    Check ($stage+' payload executable removed') (-not (Test-Path -LiteralPath (Join-Path $path 'WorkBookmark.exe')))
    Check ($stage+' no registered product') (@(Products).Count -eq 0)
    Check ($stage+' installer registration removed') (-not (Test-Path $key))
    Check ($stage+' start menu removed') (-not (Test-Path $menu))
    Check ($stage+' startup shortcut removed') (-not (Test-Path $startup))
    Preserved $stage
}
try {
    $activePath = $custom
    $installed = $msi
    Msi 'fresh-custom' @('/i',('"'+$msi+'"'),('INSTALLFOLDER="'+$custom+'"'))
    InstalledAt $custom 'custom install'
    Check 'custom install does not create default directory' (-not (Test-Path $default))
    # An unrelated file inside the owned test install folder must survive removal.
    $foreign = Join-Path $custom 'keep-user-file.txt'
    [IO.File]::WriteAllText($foreign,'Owned lifecycle fixture: preserve this file.')
    $foreignHash = (Get-FileHash -LiteralPath $foreign).Hash
    # Remove only the exact test-owned executable to prove repair uses the saved path.
    $target = [IO.Path]::GetFullPath((Join-Path $custom 'WorkBookmark.exe'))
    if (-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Repair fixture outside evidence root.' }
    Remove-Item -LiteralPath $target
    Msi 'repair-custom-without-path' @('/fa',('"'+$msi+'"'))
    InstalledAt $custom 'repair without path'
    Check 'repair does not create default directory' (-not (Test-Path $default))
    $different = Join-Path $root 'must-not-relocate'
    Msi 'repair-custom-conflicting-path' @('/fa',('"'+$msi+'"'),('INSTALLFOLDER="'+$different+'"'))
    InstalledAt $custom 'repair conflicting path'
    Check 'maintenance cannot relocate payload' (-not (Test-Path $different))
    # A local, unsigned successor fixture reuses the exact candidate payload and
    # installer actions. Only package/product identity and upgrade bounds change.
    # It tests next-version MSI removal/search behavior; it is not a 0.2.5 release.
    $successor = Join-Path $root 'successor-fixture-only.msi'
    Copy-Item -LiteralPath $msi -Destination $successor
    $api = New-Object -ComObject WindowsInstaller.Installer
    try {
        $db = $api.OpenDatabase($successor,1)
        try {
            $newProduct = '{'+[guid]::NewGuid().ToString().ToUpperInvariant()+'}'
            foreach ($query in @(
                'UPDATE `Property` SET `Value` = ''0.2.5'' WHERE `Property` = ''ProductVersion''',
                ('UPDATE `Property` SET `Value` = ''{0}'' WHERE `Property` = ''ProductCode''' -f $newProduct)
            )) {
                $view = $db.OpenView($query)
                try { [void]$view.Execute() }
                finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
            # VersionMin/Max belong to the Upgrade table primary key. MSI SQL
            # cannot UPDATE key columns: retain the full row and replace it.
            foreach ($action in @('WIX_UPGRADE_DETECTED','WIX_DOWNGRADE_DETECTED')) {
                $view = $db.OpenView(('SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`, `Remove`, `ActionProperty` FROM `Upgrade` WHERE `ActionProperty` = ''{0}''' -f $action))
                try { [void]$view.Execute(); $record = $view.Fetch() }
                finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
                try {
                    if ($null -eq $record) { throw 'Missing fixture upgrade row.' }
                    $column = if ($action -eq 'WIX_UPGRADE_DETECTED') { 3 } else { 2 }
                    $record.StringData($column) = '0.2.5'
                    $view = $db.OpenView(('DELETE FROM `Upgrade` WHERE `ActionProperty` = ''{0}''' -f $action))
                    try { [void]$view.Execute() }
                    finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
                    $view = $db.OpenView('INSERT INTO `Upgrade` (`UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`, `Remove`, `ActionProperty`) VALUES (?, ?, ?, ?, ?, ?, ?)')
                    try { [void]$view.Execute($record) }
                    finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
                } finally { if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) } }
            }
            $summary = $db.SummaryInformation(1)
            try { $summary.Property(9) = '{'+[guid]::NewGuid().ToString().ToUpperInvariant()+'}'; $summary.Persist() }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
            $db.Commit()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($api) }
    $installed = $successor
    Msi 'remembered-custom-successor-upgrade' @('/i',('"'+$successor+'"'))
    InstalledAt $custom 'successor fixture upgrade' '0.2.5'
    Check 'successor upgrade does not create default directory' (-not (Test-Path $default))
    Msi 'remembered-custom-successor-repair' @('/fa',('"'+$successor+'"'))
    InstalledAt $custom 'successor fixture repair' '0.2.5'
    Msi 'uninstall-custom-without-path' @('/x',('"'+$successor+'"'))
    $installed = $null
    Removed $custom 'custom uninstall'
    Check 'uninstall preserves unrelated file' ((Get-FileHash -LiteralPath $foreign).Hash -eq $foreignHash)
    Check 'uninstall removes every other payload file' (@(Get-ChildItem -LiteralPath $custom -Recurse -Force -File | Where-Object FullName -ne $foreign).Count -eq 0)
    Check 'custom lifecycle leaves default directory absent' (-not (Test-Path $default))

    $activePath = $default
    $installed = $previous
    Msi 'legacy-default-install' @('/i',('"'+$previous+'"'))
    Preserved 'legacy default install'
    $installed = $msi
    Msi 'legacy-default-upgrade' @('/i',('"'+$msi+'"'))
    InstalledAt $default 'default upgrade'
    Msi 'default-upgrade-repair' @('/fa',('"'+$msi+'"'))
    InstalledAt $default 'default repair'
    Msi 'default-upgrade-uninstall' @('/x',('"'+$msi+'"'))
    $installed = $null
    Removed $default 'default uninstall'
    Check 'default payload directory removed' (-not (Test-Path $default))

    # Releases <=0.2.3 did not persist their location. Their own cached MSI cannot
    # inherit the new search during major-upgrade removal. Migrate explicitly.
    $legacy = Join-Path $root 'legacy custom'
    $activePath = $legacy
    $installed = $previous
    Msi 'legacy-custom-install' @('/i',('"'+$previous+'"'),('INSTALLFOLDER="'+$legacy+'"'))
    Preserved 'legacy custom install'
    Msi 'legacy-custom-remove-with-path' @('/x',('"'+$previous+'"'),('INSTALLFOLDER="'+$legacy+'"'))
    $installed = $null
    Removed $legacy 'legacy custom explicit removal'
    Check 'legacy custom payload directory removed' (-not (Test-Path $legacy))
    $installed = $msi
    Msi 'migrate-custom-024' @('/i',('"'+$msi+'"'),('INSTALLFOLDER="'+$legacy+'"'))
    InstalledAt $legacy 'migrated custom install'
    Msi 'migrated-custom-remove' @('/x',('"'+$msi+'"'))
    $installed = $null
    Removed $legacy 'migrated custom uninstall'
    Check 'migrated custom directory removed' (-not (Test-Path $legacy))
    $status = 'passed'
} catch { $status='failed'; $failure=$_.Exception.Message; throw }
finally {
    try {
        if ($installed) {
            # An unsuccessful upgrade may leave the old product registered.
            # Preflight established that every product in this family is test-owned.
            foreach ($product in @(Products)) {
                Msi ('cleanup-'+$product.version) @('/x',$product.code,('INSTALLFOLDER="'+$activePath+'"'))
            }
            Preserved 'cleanup'
        }
    } finally {
        @{status=$status;error=$failure;checks=$checks.ToArray();candidateSha256=$ExpectedSha256;previousVersion='0.2.3';dataContentsLogged=$false;uiAndIme='NOT_RUN'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding UTF8
        Write-Output $root
    }
}
