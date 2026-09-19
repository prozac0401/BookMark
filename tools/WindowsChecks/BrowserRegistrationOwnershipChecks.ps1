# Read-only registration policy checks. Registry objects are replaced by in-memory doubles.
. (Join-Path $PSScriptRoot '..\..\scripts\browser-registration-common.ps1')
$entry = @(Get-WorkBookmarkBrowserEntries 'Chrome')[0]
$script:fakeEntries = @{}
function Open-WorkBookmarkRegistry([Microsoft.Win32.RegistryHive] $Hive, [Microsoft.Win32.RegistryView] $View) {
 $record = $script:fakeEntries[($Hive.ToString() + ':' + $View.ToString())]
 $base = [pscustomobject]@{ Record = $record }
 $base | Add-Member ScriptMethod OpenSubKey { param($path,$write) return $this.Record }
 $base | Add-Member ScriptMethod Dispose { }
 return $base
}
function FakeKey($manifest, $owner) {
 $key = [pscustomobject]@{ Manifest = $manifest; Owner = $owner }
 $key | Add-Member ScriptMethod GetValue { param($name,$fallback,$options) if ($name -eq '') { return $this.Manifest }; if ($name -eq 'WorkBookmarkOwner') { return $this.Owner }; return $fallback }
 $key | Add-Member ScriptMethod Dispose { }
 return $key
}
function AssertResult($expected,$name) {
 $actual = Assert-WorkBookmarkOwnedRegistration $entry
 if ($actual -ne $expected) { throw $name }; Write-Output ('PASS ' + $name)
}
function AssertRefused($name) {
 $refused = $false
 try { [void](Assert-WorkBookmarkOwnedRegistration $entry -CheckMachine) } catch { $refused = $true }
 if (-not $refused) { throw $name }; Write-Output ('PASS ' + $name)
}
AssertResult $false 'no registration remains unowned'
$owned = FakeKey $entry.ManifestPath 'WorkBookmark.NativeMessaging.v1'
$foreign = FakeKey 'C:oreignhost.json' 'OtherOwner'
$script:fakeEntries = @{ 'CurrentUser:Registry32' = $owned }; AssertResult $true '32-bit owned registration recognized'
$script:fakeEntries = @{ 'CurrentUser:Registry64' = $owned }; AssertResult $true '64-bit owned registration recognized'
$script:fakeEntries = @{ 'CurrentUser:Registry32' = $owned; 'CurrentUser:Registry64' = $owned }; AssertResult $true 'both owned views recognized'
$script:fakeEntries = @{ 'CurrentUser:Registry32' = $foreign; 'CurrentUser:Registry64' = $owned }; AssertRefused 'foreign 32-bit shadow is refused'
$script:fakeEntries = @{ 'CurrentUser:Registry32' = $owned; 'CurrentUser:Registry64' = $foreign }; AssertRefused 'foreign 64-bit shadow is refused'
$script:fakeEntries = @{ 'CurrentUser:Registry64' = $owned; 'LocalMachine:Registry32' = $foreign }; AssertRefused 'machine-wide shadow is refused'
Write-Output 'RESULT: seven mock ownership checks; no registry accessed or modified.'
