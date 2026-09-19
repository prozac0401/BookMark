[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string] $HostExecutable,
    [ValidateSet('Chrome', 'Edge', 'Both')][string] $Browser = 'Both',
    [string] $ChromeExtensionId,
    [string] $EdgeExtensionId
)
. (Join-Path $PSScriptRoot 'browser-registration-common.ps1')

# Use the actual ID shown by each browser. No wildcard or guessed ID is accepted.
if ($Browser -in @('Chrome', 'Both') -and $ChromeExtensionId -cnotmatch '^[a-p]{32}$') {
    throw 'Provide -ChromeExtensionId with the actual 32-letter ID from chrome://extensions.'
}
if ($Browser -in @('Edge', 'Both') -and $EdgeExtensionId -cnotmatch '^[a-p]{32}$') {
    throw 'Provide -EdgeExtensionId with the actual 32-letter ID from edge://extensions.'
}
$hostItem = Get-Item -LiteralPath $HostExecutable
if ($hostItem.PSIsContainer -or $hostItem.Extension -ine '.exe' -or $hostItem.Name -ine 'WorkBookmark.BrowserHost.exe' -or
    ($hostItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw '-HostExecutable must point to the built WorkBookmark.BrowserHost.exe, not the UI executable, a link, or a command line.'
}
$hostPath = $hostItem.FullName
$entries = @(Get-WorkBookmarkBrowserEntries $Browser)
Assert-WorkBookmarkRegistrationDirectory
foreach ($entry in $entries) {
    $owned = Assert-WorkBookmarkOwnedRegistration $entry -CheckMachine
    Assert-WorkBookmarkManifest $entry $owned
}
if (-not $PSCmdlet.ShouldProcess("$Browser for the current Windows user", 'Register WorkBookmark browser messaging host')) { return }

$root = Get-WorkBookmarkBrowserRoot
[void][IO.Directory]::CreateDirectory($root)
$completed = New-Object 'System.Collections.Generic.List[object]'
try {
    foreach ($entry in $entries) {
        $extensionId = if ($entry.Browser -eq 'Chrome') { $ChromeExtensionId } else { $EdgeExtensionId }
        $manifest = [ordered]@{
            name = 'com.workbookmark.capture'
            description = 'WorkBookmark current-page capture'
            path = $hostPath
            type = 'stdio'
            allowed_origins = @("chrome-extension://$extensionId/")
        }
        # There is no native-host manifest "args" field. BrowserHost.exe accepts the browser-supplied origin.
        $oldBytes = if ([IO.File]::Exists($entry.ManifestPath)) { [IO.File]::ReadAllBytes($entry.ManifestPath) } else { $null }
        $oldOwned = Assert-WorkBookmarkOwnedRegistration $entry -CheckMachine
        Assert-WorkBookmarkManifest $entry $oldOwned
        $entry | Add-Member -NotePropertyName OldBytes -NotePropertyValue $oldBytes
        $view = if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
        $beforeBase = Open-WorkBookmarkRegistry ([Microsoft.Win32.RegistryHive]::CurrentUser) $view
        $beforeKey = $null
        try {
            $beforeKey = $beforeBase.OpenSubKey($entry.RegistryPath, $false)
            $entry | Add-Member -NotePropertyName OldOwned -NotePropertyValue ($null -ne $beforeKey)
        } finally { if ($null -ne $beforeKey) { $beforeKey.Dispose() }; $beforeBase.Dispose() }
        $completed.Add($entry)
        $json = $manifest | ConvertTo-Json -Depth 4
        [IO.File]::WriteAllText($entry.ManifestPath, $json, (New-Object Text.UTF8Encoding($false)))
        $view = if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
        $base = Open-WorkBookmarkRegistry ([Microsoft.Win32.RegistryHive]::CurrentUser) $view
        $key = $null
        try {
            $key = $base.CreateSubKey($entry.RegistryPath, $true)
            $currentManifest = $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $currentOwner = $key.GetValue('WorkBookmarkOwner', $null)
            $newEmptyKey = -not $entry.OldOwned -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
            if (-not $newEmptyKey -and ($currentManifest -ne $entry.ManifestPath -or $currentOwner -ne 'WorkBookmark.NativeMessaging.v1')) {
                throw "Registration changed for $($entry.Browser); stopping without replacing it."
            }
            $key.SetValue('', $entry.ManifestPath, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('WorkBookmarkOwner', 'WorkBookmark.NativeMessaging.v1', [Microsoft.Win32.RegistryValueKind]::String)
        } finally {
            if ($null -ne $key) { $key.Dispose() }
            $base.Dispose()
        }
        Write-Output "$($entry.Browser): registered $($entry.ManifestPath)"
    }
} catch {
    $failure = $_
    # Roll back only entries preflighted as ours (or newly created); never remove parent registry trees.
    foreach ($entry in $completed) {
        try {
            # Preserve a registration replaced after preflight, including its current manifest.
            $rollbackView = if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
            $rollbackBase = Open-WorkBookmarkRegistry ([Microsoft.Win32.RegistryHive]::CurrentUser) $rollbackView
            $rollbackKey = $null
            try {
                $rollbackKey = $rollbackBase.OpenSubKey($entry.RegistryPath, $false)
                if ($null -ne $rollbackKey -and ($rollbackKey.ValueCount -ne 0 -or $rollbackKey.SubKeyCount -ne 0) -and
                    ($rollbackKey.GetValue('') -ne $entry.ManifestPath -or $rollbackKey.GetValue('WorkBookmarkOwner') -ne 'WorkBookmark.NativeMessaging.v1')) {
                    throw 'Registration ownership changed or is incomplete; automatic rollback will not alter it.'
                }
            } finally { if ($null -ne $rollbackKey) { $rollbackKey.Dispose() }; $rollbackBase.Dispose() }
            if (-not $entry.OldOwned) {
                foreach ($view in @($(if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }))) {
                    $base = Open-WorkBookmarkRegistry ([Microsoft.Win32.RegistryHive]::CurrentUser) $view
                    $key = $null
                    try {
                        $key = $base.OpenSubKey($entry.RegistryPath, $true)
                        if ($null -eq $key) { continue }
                        if ($key.GetValue('') -eq $entry.ManifestPath -and $key.GetValue('WorkBookmarkOwner') -eq 'WorkBookmark.NativeMessaging.v1') {
                            $key.DeleteValue('', $false)
                            if ($key.GetValue('WorkBookmarkOwner') -eq 'WorkBookmark.NativeMessaging.v1') { $key.DeleteValue('WorkBookmarkOwner', $false) }
                            $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
                            $key.Dispose(); $key = $null
                            if ($empty) { $base.DeleteSubKey($entry.RegistryPath, $false) }
                        }
                    } finally { if ($null -ne $key) { $key.Dispose() }; $base.Dispose() }
                }
            }
            if ($null -eq $entry.OldBytes) { if ([IO.File]::Exists($entry.ManifestPath)) { [IO.File]::Delete($entry.ManifestPath) } }
            else { [IO.File]::WriteAllBytes($entry.ManifestPath, [byte[]] $entry.OldBytes) }
        } catch { Write-Warning "Registration rollback needs review for $($entry.Browser): $($_.Exception.Message)" }
    }
    throw $failure
}
Write-Output 'Run WorkBookmark, then use the extension on an ordinary http/https page. This script does not install the extension.'
