Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WorkBookmarkBrowserRoot {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable.' }
    return [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'WorkBookmark\BrowserHosts'))
}
function Get-WorkBookmarkBrowserEntries([string] $Browser) {
    $root = Get-WorkBookmarkBrowserRoot
    $names = if ($Browser -eq 'Both') { @('Chrome', 'Edge') } else { @($Browser) }
    foreach ($name in $names) {
        $vendor = if ($name -eq 'Chrome') { 'Google\Chrome' } else { 'Microsoft\Edge' }
        [pscustomobject]@{
            Browser = $name
            RegistryPath = "Software\$vendor\NativeMessagingHosts\com.workbookmark.capture"
            ManifestPath = Join-Path $root ($name.ToLowerInvariant() + '.com.workbookmark.capture.json')
        }
    }
}
function Open-WorkBookmarkRegistry([Microsoft.Win32.RegistryHive] $Hive, [Microsoft.Win32.RegistryView] $View) {
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
}
function Get-WorkBookmarkRegistryViews {
    if ([Environment]::Is64BitOperatingSystem) { return @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64) }
    return @([Microsoft.Win32.RegistryView]::Registry32)
}
function Assert-WorkBookmarkOwnedRegistration($Entry, [switch] $CheckMachine) {
    $foundOwned = $false
    foreach ($view in (Get-WorkBookmarkRegistryViews)) {
        $hives = if ($CheckMachine) { @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine) } else { @([Microsoft.Win32.RegistryHive]::CurrentUser) }
        foreach ($hive in $hives) {
            $base = Open-WorkBookmarkRegistry $hive $view
            $key = $null
            try {
                $key = $base.OpenSubKey($Entry.RegistryPath, $false)
                if ($null -eq $key) { continue }
                if ($hive -eq [Microsoft.Win32.RegistryHive]::LocalMachine) {
                    throw "A machine-wide $($Entry.Browser) host registration already exists; it will not be shadowed."
                }
                $manifest = $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $owner = $key.GetValue('WorkBookmarkOwner', $null)
                if ($owner -ne 'WorkBookmark.NativeMessaging.v1' -or
                    $manifest -isnot [string] -or
                    -not [string]::Equals($manifest, $Entry.ManifestPath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "An unowned $($Entry.Browser) host registration exists; it will not be changed."
                }
                $foundOwned = $true
            } finally {
                if ($null -ne $key) { $key.Dispose() }
                $base.Dispose()
            }
        }
    }
    return $foundOwned
}
function Assert-WorkBookmarkManifest($Entry, [bool] $Owned) {
    if (-not [IO.File]::Exists($Entry.ManifestPath)) { return }
    if (-not $Owned) { throw "An unowned manifest already exists at $($Entry.ManifestPath)." }
    $item = Get-Item -LiteralPath $Entry.ManifestPath
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A manifest link is not supported.' }
    try { $manifest = [IO.File]::ReadAllText($Entry.ManifestPath) | ConvertFrom-Json }
    catch { throw "The existing manifest is unreadable; it will not be changed: $($Entry.ManifestPath)" }
    if ($manifest.name -ne 'com.workbookmark.capture' -or $manifest.type -ne 'stdio') {
        throw "The existing manifest has another purpose; it will not be changed: $($Entry.ManifestPath)"
    }
}
function Assert-WorkBookmarkRegistrationDirectory {
    $root = Get-WorkBookmarkBrowserRoot
    $parent = [IO.Path]::GetDirectoryName($root)
    foreach ($directory in @($parent, $root)) {
        if (Test-Path -LiteralPath $directory) {
            $item = Get-Item -LiteralPath $directory
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The registration directory must be a normal local directory: $directory"
            }
        }
    }
}
