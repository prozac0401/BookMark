param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$DotNetPath
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
if (-not $publishPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must be inside the repository.' }
foreach ($required in @('WorkBookmark.exe', 'WorkBookmark.dll', 'coreclr.dll', 'hostpolicy.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required))) { throw "Missing self-contained publish file: $required" }
}
$version = ((Get-Item -LiteralPath (Join-Path $publishPath 'WorkBookmark.exe')).VersionInfo.ProductVersion -split '\+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'MSI version must be a three-part numeric version.' }
$versionParts = $version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) { throw 'Version exceeds Windows Installer limits.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'artifacts/installer' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $outputPath.StartsWith($publishPath + '\', [StringComparison]::OrdinalIgnoreCase) -or $outputPath -eq $publishPath) { throw 'Output must be inside the repository and outside the publish directory.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$workPath = Join-Path $outputPath ('obj-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workPath | Out-Null

if (-not $DotNetPath) {
    $DotNetPath = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotNetPath)) { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotNetPath = (Resolve-Path -LiteralPath $DotNetPath).Path
$wixVersion = '4.0.6'
$wixDirectory = Join-Path $taskRoot '.tools/wix'
$wix = Join-Path $wixDirectory 'wix.exe'
$oldDotnetRoot = $env:DOTNET_ROOT
$oldRollForward = $env:DOTNET_ROLL_FORWARD
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetPath
    # WiX 4's build-only .NET tool can use the repository SDK runtime. The MSI embeds no WiX executable.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    if (-not (Test-Path -LiteralPath $wix)) {
        & $DotNetPath tool install wix --tool-path $wixDirectory --version $wixVersion --configfile (Join-Path $taskRoot 'NuGet.Config') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'WiX tool installation failed.' }
    }
    $actualWix = & $wix --version
    if ($LASTEXITCODE -ne 0 -or $actualWix -notmatch '^4\.0\.6(\+|$)') { throw 'MSI build requires pinned WiX 4.0.6.' }
    # Keep build extensions local to .tools and pin them to the same release as the compiler.
    $extensionPaths = @()
    foreach ($extension in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
        $extensionPath = Join-Path $wixDirectory ".wix/extensions/$extension/$wixVersion/wixext4/$extension.dll"
        if (-not (Test-Path -LiteralPath $extensionPath)) {
            Push-Location $wixDirectory
            try {
                & $wix extension add "$extension/$wixVersion" | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "WiX extension installation failed: $extension/$wixVersion" }
            }
            finally { Pop-Location }
        }
        if (-not (Test-Path -LiteralPath $extensionPath)) { throw "Missing pinned WiX extension: $extensionPath" }
        $extensionPaths += @('-ext', $extensionPath)
    }

    function StableHash([string]$value) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('WorkBookmark MSI v1|' + $value.ToLowerInvariant())))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    function StableGuid([string]$value) {
        $hash = StableHash $value
        return $hash.Substring(0,8) + '-' + $hash.Substring(8,4) + '-' + $hash.Substring(12,4) + '-' + $hash.Substring(16,4) + '-' + $hash.Substring(20,12)
    }
    $entries = @(Get-ChildItem -LiteralPath $publishPath -Recurse -Force)
    if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Publish payload must not contain reparse points.' }
    $files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'Publish payload is empty.' }
    $xmlSettings = New-Object Xml.XmlWriterSettings
    $xmlSettings.Indent = $true
    $xmlSettings.Encoding = New-Object Text.UTF8Encoding($false)
    $manifestPath = Join-Path $workPath 'PublishedFiles.wxs'
    $writer = [Xml.XmlWriter]::Create($manifestPath, $xmlSettings)
    $namespace = 'http://wixtoolset.org/schemas/v4/wxs'
    $componentIds = [Collections.Generic.List[string]]::new()
    $directoryIds = @{ '' = 'INSTALLFOLDER' }
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('Wix', $namespace)
        $writer.WriteStartElement('Fragment', $namespace)
        # Directory and component identities depend only on relative paths, never machine paths or version.
        foreach ($directory in @($entries | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length }, FullName)) {
            $relative = $directory.FullName.Substring($publishPath.Length + 1)
            $parent = [IO.Path]::GetDirectoryName($relative)
            $id = 'dir_' + (StableHash ('directory|' + $relative)).Substring(0,32)
            $directoryIds[$relative] = $id
            $writer.WriteStartElement('DirectoryRef', $namespace); $writer.WriteAttributeString('Id', $directoryIds[$parent])
            $writer.WriteStartElement('Directory', $namespace); $writer.WriteAttributeString('Id', $id); $writer.WriteAttributeString('Name', $directory.Name)
            $writer.WriteEndElement(); $writer.WriteEndElement()
        }
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($publishPath.Length + 1)
            $directoryId = $directoryIds[[IO.Path]::GetDirectoryName($relative)]
            $hash = (StableHash ('file|' + $relative)).Substring(0,32)
            $componentId = 'cmp_' + $hash; $componentIds.Add($componentId)
            $fileId = if ($relative -eq 'WorkBookmark.exe') { 'WorkBookmarkExecutable' } else { 'file_' + $hash }
            $writer.WriteStartElement('DirectoryRef', $namespace); $writer.WriteAttributeString('Id', $directoryId)
            $writer.WriteStartElement('Component', $namespace); $writer.WriteAttributeString('Id', $componentId)
            $writer.WriteAttributeString('Guid', (StableGuid ('component|' + $relative))); $writer.WriteAttributeString('Bitness', 'always64')
            $writer.WriteStartElement('File', $namespace); $writer.WriteAttributeString('Id', $fileId); $writer.WriteAttributeString('Source', $file.FullName); $writer.WriteAttributeString('Name', $file.Name); $writer.WriteEndElement()
            # HKCU key paths are required for components installed under a user's profile (ICE38).
            $writer.WriteStartElement('RegistryValue', $namespace); $writer.WriteAttributeString('Root', 'HKCU')
            $writer.WriteAttributeString('Key', 'Software\WorkBookmark\Installer\Files'); $writer.WriteAttributeString('Name', $hash)
            $writer.WriteAttributeString('Type', 'integer'); $writer.WriteAttributeString('Value', '1'); $writer.WriteAttributeString('KeyPath', 'yes'); $writer.WriteEndElement()
            $writer.WriteEndElement(); $writer.WriteEndElement()
        }
        $componentIds.Add('PayloadDirectories')
        $writer.WriteStartElement('DirectoryRef', $namespace); $writer.WriteAttributeString('Id', 'INSTALLFOLDER')
        $writer.WriteStartElement('Component', $namespace); $writer.WriteAttributeString('Id', 'PayloadDirectories')
        $writer.WriteAttributeString('Guid', (StableGuid 'payload-directories')); $writer.WriteAttributeString('Bitness', 'always64')
        foreach ($id in @($directoryIds.Values | Sort-Object)) {
            $writer.WriteStartElement('RemoveFolder', $namespace); $writer.WriteAttributeString('Id', 'remove_' + $id)
            $writer.WriteAttributeString('Directory', $id); $writer.WriteAttributeString('On', 'uninstall'); $writer.WriteEndElement()
        }
        $writer.WriteStartElement('RegistryValue', $namespace); $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\WorkBookmark\Installer'); $writer.WriteAttributeString('Name', 'Directories')
        $writer.WriteAttributeString('Type', 'integer'); $writer.WriteAttributeString('Value', '1'); $writer.WriteAttributeString('KeyPath', 'yes'); $writer.WriteEndElement()
        $writer.WriteEndElement(); $writer.WriteEndElement()
        $writer.WriteStartElement('ComponentGroup', $namespace); $writer.WriteAttributeString('Id', 'PublishedFiles')
        foreach ($id in $componentIds) { $writer.WriteStartElement('ComponentRef', $namespace); $writer.WriteAttributeString('Id', $id); $writer.WriteEndElement() }
        $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndDocument()
    }
    finally { $writer.Dispose() }
    $msiPath = Join-Path $outputPath "WorkBookmark-$version-win-x64.msi"
    & $wix build (Join-Path $taskRoot 'installer/Package.wxs') (Join-Path $taskRoot 'installer/WorkBookmarkUI.wxs') (Join-Path $taskRoot 'installer/WorkBookmarkUI.ko-kr.wxl') $manifestPath @extensionPaths -culture ko-kr -arch x64 -d "Version=$version" -d "ProductCode=$(StableGuid ('product|' + $version))" -d "PublishDirectory=$publishPath" -intermediatefolder $workPath -pdbtype none -out $msiPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'MSI build or Windows Installer validation failed.' }
    & (Join-Path $PSScriptRoot 'test-msi.ps1') -MsiPath $msiPath -PublishDirectory $publishPath | Out-Host
    Write-Output $msiPath
}
finally { $env:DOTNET_ROOT = $oldDotnetRoot; $env:DOTNET_ROLL_FORWARD = $oldRollForward }
