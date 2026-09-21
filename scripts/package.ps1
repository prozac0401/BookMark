param([Parameter(Mandatory=$true)][string]$PublishDirectory, [string]$GitPath, [string]$MsiPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not $publishPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must be within this repository.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe'))) { throw 'Missing built executable.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$releaseDirectory = Join-Path $taskRoot ('artifacts/releases/' + $stamp)
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$packageVersion = ((Get-Item -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe')).VersionInfo.ProductVersion -split '\+')[0]
if ($packageVersion -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Invalid package version.' }
$binaryZip = Join-Path $releaseDirectory ("WorkBookmark-$packageVersion-win-x64.zip")
[IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $binaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sourceZip = Join-Path $releaseDirectory 'Source.zip'
if (-not $GitPath) { $GitPath = (Get-Command git -ErrorAction Stop).Source }
& $GitPath -C $taskRoot diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Commit tracked changes before packaging so Source.zip matches the release commit.' }
& $GitPath -C $taskRoot archive --format=zip --output=$sourceZip HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
$packageFiles = @($binaryZip, $sourceZip, (Join-Path $publishPath 'WorkBookmark.exe'))
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
