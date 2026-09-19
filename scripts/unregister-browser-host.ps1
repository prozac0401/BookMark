[CmdletBinding(SupportsShouldProcess = $true)]
param([ValidateSet('Chrome', 'Edge', 'Both')][string] $Browser = 'Both')
. (Join-Path $PSScriptRoot 'browser-registration-common.ps1')

$entries = @(Get-WorkBookmarkBrowserEntries $Browser)
Assert-WorkBookmarkRegistrationDirectory
foreach ($entry in $entries) {
    $owned = Assert-WorkBookmarkOwnedRegistration $entry
    Assert-WorkBookmarkManifest $entry $owned
    $entry | Add-Member -NotePropertyName Owned -NotePropertyValue $owned
}
if (-not $PSCmdlet.ShouldProcess("$Browser for the current Windows user", 'Remove only WorkBookmark-owned browser host registration')) { return }
foreach ($entry in $entries) {
    if (-not $entry.Owned) { Write-Output "$($entry.Browser): no owned registration."; continue }
    # Recheck immediately before removal; another installer may have replaced the value.
    [void](Assert-WorkBookmarkOwnedRegistration $entry)
    $removedAny = $false
    foreach ($view in (Get-WorkBookmarkRegistryViews)) {
        $base = Open-WorkBookmarkRegistry ([Microsoft.Win32.RegistryHive]::CurrentUser) $view
        $key = $null
        try {
            $key = $base.OpenSubKey($entry.RegistryPath, $true)
            if ($null -eq $key) { continue }
            if ($removedAny -and $null -eq $key.GetValue('') -and $null -eq $key.GetValue('WorkBookmarkOwner')) {
                # HKCU Software is normally shared across views. Preserve any unrelated values left after our first removal.
                continue
            }
            if ($key.GetValue('') -ne $entry.ManifestPath -or $key.GetValue('WorkBookmarkOwner') -ne 'WorkBookmark.NativeMessaging.v1') {
                throw "Registration changed for $($entry.Browser); stopping without removing it."
            }
            $key.DeleteValue('', $false)
            $key.DeleteValue('WorkBookmarkOwner', $false)
            $removedAny = $true
            $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
            $key.Dispose(); $key = $null
            if ($empty) { $base.DeleteSubKey($entry.RegistryPath, $false) }
        } finally { if ($null -ne $key) { $key.Dispose() }; $base.Dispose() }
    }
    Assert-WorkBookmarkManifest $entry $true
    if ([IO.File]::Exists($entry.ManifestPath)) { [IO.File]::Delete($entry.ManifestPath) }
    Write-Output "$($entry.Browser): removed owned registration and manifest."
}
Write-Output 'The extension, app executable, and saved bookmarks were not removed.'
