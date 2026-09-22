param([Parameter(Mandatory=$true)][string]$PublishDirectory, [string]$GitPath, [string]$MsiPath,
      [ValidateSet('Commit', 'WorkingTree')][string]$SourceMode = 'Commit')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not $publishPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must be within this repository.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe'))) { throw 'Missing built executable.' }
if (-not $GitPath) { $GitPath = (Get-Command git -ErrorAction Stop).Source }
function ReadGit([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($GitPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    # ReadGit uses only fixed Git switches/refs; quote the Windows directory separately.
    # Arguments keeps this build script compatible with Windows PowerShell 5.1 as well.
    if (@($Arguments | Where-Object { $_ -notmatch '^[-A-Za-z0-9=]+$' }).Count -ne 0) { throw 'Unexpected Git argument.' }
    $start.Arguments = '-C "' + $taskRoot + '" ' + ($Arguments -join ' ')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Cannot read source repository state.' }
        return $output
    }
    finally { $process.Dispose() }
}
$baseCommit = (ReadGit @('rev-parse', 'HEAD')).Trim()
$sourceStatus = ReadGit @('status', '--porcelain', '--untracked-files=normal')
if ($SourceMode -eq 'Commit' -and $sourceStatus.Length -ne 0) {
    throw 'Commit all source changes before packaging, or use -SourceMode WorkingTree to explicitly archive the current source snapshot.'
}
# Git supplies the source allowlist, excluding tools, generated artifacts and ignored local data.
# NUL framing preserves Unicode, spaces and other legal filename characters.
$sourcePaths = @()
if ($SourceMode -eq 'WorkingTree') {
    $sourcePaths = @((ReadGit @('ls-files', '--cached', '--others', '--exclude-standard', '-z')).Split([char]0,
        [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique)
    foreach ($relative in $sourcePaths) {
        $sourcePath = [IO.Path]::GetFullPath((Join-Path $taskRoot $relative))
        if (-not $sourcePath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source snapshot path escaped the repository.'
        }
        if (Test-Path -LiteralPath $sourcePath) {
            $sourceItem = Get-Item -LiteralPath $sourcePath -Force
            if ($sourceItem.PSIsContainer -or ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Source snapshots require ordinary repository files.'
            }
            $parent = $sourceItem.Directory
            while ($null -ne $parent -and $parent.FullName -ne $taskRoot) {
                if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source snapshots cannot follow linked directories.' }
                $parent = $parent.Parent
            }
        }
    }
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$releaseDirectory = Join-Path $taskRoot ('artifacts/releases/' + $stamp)
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$packageVersion = ((Get-Item -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe')).VersionInfo.ProductVersion -split '\+')[0]
if ($packageVersion -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Invalid package version.' }
$binaryZip = Join-Path $releaseDirectory ("WorkBookmark-$packageVersion-win-x64.zip")
[IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $binaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sourceZip = Join-Path $releaseDirectory 'Source.zip'
if ($SourceMode -eq 'Commit') {
    & $GitPath -C $taskRoot archive --format=zip --output=$sourceZip HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
}
else {
    $archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $sourcePaths) {
            $sourcePath = Join-Path $taskRoot $relative
            # Tracked deletions are absent from the snapshot, just as they are from this build.
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sourcePath,
                $relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}
$sourceMetadata = Join-Path $releaseDirectory 'SourceSnapshot.json'
[ordered]@{
    productVersion = $packageVersion
    sourceMode = $SourceMode
    baseCommit = $baseCommit
    workingTreeHadChanges = $sourceStatus.Length -ne 0
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceSha256 = (Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath $sourceMetadata -Encoding UTF8
$packageFiles = @($binaryZip, $sourceZip, $sourceMetadata, (Join-Path $publishPath 'WorkBookmark.exe'))
if ($MsiPath) {
    $msiSource = (Resolve-Path -LiteralPath $MsiPath).Path
    if ([IO.Path]::GetFileName($msiSource) -ne "WorkBookmark-$packageVersion-win-x64.msi") { throw 'MSI filename does not match the package version.' }
    & (Join-Path $PSScriptRoot 'test-msi.ps1') -MsiPath $msiSource -PublishDirectory $publishPath -Extract | Out-Host
    $msiDestination = Join-Path $releaseDirectory ([IO.Path]::GetFileName($msiSource))
    Copy-Item -LiteralPath $msiSource -Destination $msiDestination
    $validationSource = [IO.Path]::ChangeExtension($msiSource, '.validation.json')
    $validationDestination = Join-Path $releaseDirectory ([IO.Path]::GetFileName($validationSource))
    Copy-Item -LiteralPath $validationSource -Destination $validationDestination
    $packageFiles += @($msiDestination, $validationDestination)
}
$hashes = $packageFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
$hashes | ForEach-Object { $_.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output $releaseDirectory
