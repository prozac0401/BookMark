$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $taskRoot 'docs/evidence/sdk-download.json') -Raw | ConvertFrom-Json
$sdkDirectory = Join-Path $taskRoot '.tools/dotnet'
$archive = Join-Path $taskRoot '.tools/dotnet-sdk.zip'
New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $metadata.url -OutFile $archive }
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
if ($actual -ne $metadata.hash) { throw 'SDK SHA-512 mismatch. Archive was not extracted.' }
& tar.exe -xf $archive -C $sdkDirectory
if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed.' }
& (Join-Path $sdkDirectory 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK could not start.' }
