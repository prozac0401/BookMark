param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not $publishPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must be within this repository.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe'))) { throw 'Missing built executable.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$releaseDirectory = Join-Path $taskRoot ('artifacts/releases/' + $stamp)
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$binaryZip = Join-Path $releaseDirectory 'WorkBookmark-0.1.0-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $binaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sourceZip = Join-Path $releaseDirectory 'Source.zip'
$zip = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $taskRoot -File -Recurse -Force | Where-Object {
        $relative = $_.FullName.Substring($taskRoot.Length + 1)
        $relative -notmatch '(^|[\\/])(\.git|\.tools|bin|obj|artifacts|\.artifacts|\.vs)([\\/]|$)' -and $relative -notlike '*.user' -and $_.Name -notlike '~$*'
    } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($taskRoot.Length + 1).Replace('\', '/')
        $inputStream = $null; $outputStream = $null
        try {
            # Synthetic workbooks may still be open in Excel; take a read-only shared stream.
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $inputStream = [IO.File]::Open($_.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $outputStream = $entry.Open()
            $inputStream.CopyTo($outputStream)
        }
        finally {
            if ($outputStream) { $outputStream.Dispose() }
            if ($inputStream) { $inputStream.Dispose() }
        }
    }
}
finally { $zip.Dispose() }
$hashes = @($binaryZip, $sourceZip, (Join-Path $publishPath 'WorkBookmark.exe')) | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
$hashes | ForEach-Object { $_.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output $releaseDirectory
