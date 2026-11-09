#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$WixPath,
    [Parameter(Mandatory)][string]$LegalDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$wixVersion = '7.0.1'
$wixNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
$identityNamespace = [Guid]'e586ad48-dbb2-4a41-9e09-39d7cd6c2a82'
$msiUpgradeCode = '{51789FE4-B851-4225-8E02-B09E6E2C4696}'

if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})\z' -or
    [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65534) {
    throw 'Use a canonical major.minor.patch version in the range 0.0.0 through 255.255.65534.'
}
$ReleaseDirectory = (Get-Item -LiteralPath $ReleaseDirectory).FullName
$LegalDirectory = (Get-Item -LiteralPath $LegalDirectory).FullName
$WixPath = (Get-Item -LiteralPath $WixPath).FullName
$OutputDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath))
$outputParent = [IO.Path]::GetDirectoryName($OutputDirectory)
if (!$outputParent -or !(Test-Path -LiteralPath $outputParent -PathType Container) -or
    (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'Choose a new output directory under an existing parent directory.'
}
if ($OutputDirectory.StartsWith($ReleaseDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output directory outside the Release directory.'
}

function Get-Identity([string]$Name) {
    # UUIDv5 uses the namespace and result in network byte order.
    $inputBytes = $identityNamespace.ToByteArray($true) + [Text.Encoding]::UTF8.GetBytes($Name)
    $bytes = [byte[]]([Security.Cryptography.SHA1]::HashData([byte[]]$inputBytes)[0..15])
    $bytes[6] = ($bytes[6] -band 0x0f) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3f) -bor 0x80
    [Guid]::new($bytes, $true)
}

function Add-WixElement([System.Xml.XmlNode]$Parent, [string]$Name, [hashtable]$Attributes) {
    $doc = $Parent.OwnerDocument
    if ($Parent -is [System.Xml.XmlDocument]) { $doc = $Parent }
    $element = $doc.CreateElement($Name, $wixNamespace)
    foreach ($key in $Attributes.Keys) { $element.SetAttribute($key, $Attributes[$key]) }
    [void]$Parent.AppendChild($element)
    $element
}

function Add-Component([string]$Id, [string]$Identity, [string]$Directory) {
    $component = Add-WixElement $group 'Component' @{
        Id = $Id
        Guid = (Get-Identity $Identity).ToString('B')
        Directory = $Directory
        Bitness = 'always64'
    }
    $null = Add-WixElement $component 'RegistryValue' @{
        Root = 'HKCU'
        Key = 'Software\opssh\Installer\Components'
        Name = $Id
        Type = 'integer'
        Value = '1'
        KeyPath = 'yes'
    }
    $component
}

function Invoke-Wix([string[]]$Arguments) {
    & $WixPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WiX $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

$releaseRoot = Get-Item -LiteralPath $ReleaseDirectory
if (!$releaseRoot.PSIsContainer -or ($releaseRoot.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use an ordinary Release directory.'
}
$licenseRoot = Get-Item -LiteralPath (Join-Path $LegalDirectory 'licenses')
if (!$licenseRoot.PSIsContainer -or ($licenseRoot.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use an ordinary licenses directory.'
}
$sourceFiles = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($root in @($releaseRoot, $licenseRoot)) {
    $isRelease = $root -eq $releaseRoot
    $included = 0
    foreach ($item in Get-ChildItem -LiteralPath $root.FullName -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package inputs contain a reparse point.' }
        if ($item.PSIsContainer -or ($isRelease -and $item.Extension -ieq '.pdb')) { continue }
        $relativePath = [IO.Path]::GetRelativePath($root.FullName, $item.FullName).Replace('\', '/')
        if (!$isRelease) { $relativePath = "licenses/$relativePath" }
        if ($relativePath -cnotmatch '\A[A-Za-z0-9_. -]+(?:/[A-Za-z0-9_. -]+)*\z') {
            throw 'Package inputs contain a filename outside the supported naming conventions.'
        }
        if (!$sourceFiles.TryAdd($relativePath, $item.FullName)) { throw 'Duplicate package path.' }
        $included++
    }
    if (!$included) { throw 'Expected files in both Release and licenses.' }
}
foreach ($name in @(
    'opssh-core.exe', 'opssh-tray.exe', 'opssh-tray.dll', 'opssh-tray.pri',
    'opssh-tray.deps.json', 'opssh-tray.runtimeconfig.json', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'hostfxr.dll'
)) {
    if (!$sourceFiles.ContainsKey($name) -or (Get-Item -LiteralPath $sourceFiles[$name]).Length -eq 0) {
        throw "Release is missing a nonempty $name."
    }
}
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $item = Get-Item -LiteralPath (Join-Path $LegalDirectory $name)
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or !$item.Length) {
        throw "Use a nonempty regular file for $name."
    }
    if (!$sourceFiles.TryAdd($name, $item.FullName)) { throw "Release already contains $name." }
}

$actualWixVersion = Invoke-Wix @('--version')
if ($actualWixVersion -notmatch ('\A' + [regex]::Escape($wixVersion) + '(?:\+[^ ]+)?\z')) {
    throw "Windows packages require WiX $wixVersion."
}

$stem = "opssh-$Version-windows-x64"
# Keep WiX source paths short while staging the final move on the output volume.
$work = Join-Path $outputParent ('.opssh-' + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $work | Out-Null
$payload = Join-Path $work 'payload'
$packages = Join-Path $work 'packages'
try {
    New-Item -ItemType Directory -Path $payload,$packages | Out-Null
    $document = [xml]::new()
    $wix = Add-WixElement $document 'Wix' @{}
    $fragment = Add-WixElement $wix 'Fragment' @{}
    $directoryRef = Add-WixElement $fragment 'DirectoryRef' @{ Id = 'OpsshInstallDir' }
    $group = Add-WixElement $fragment 'ComponentGroup' @{ Id = 'Payload' }
    $directories = [Collections.Generic.Dictionary[string,System.Xml.XmlElement]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories.Add('', $directoryRef)

    $rootComponent = Add-Component 'InstallDirectory' 'directory/' 'OpsshInstallDir'
    $null = Add-WixElement $rootComponent 'CreateFolder' @{}
    $null = Add-WixElement $rootComponent 'RemoveFolder' @{ Id = 'RemoveInstallDirectory'; On = 'uninstall' }

    $paths = [string[]]@($sourceFiles.Keys)
    [Array]::Sort($paths, [StringComparer]::OrdinalIgnoreCase)
    $files = foreach ($relativePath in $paths) {
        $parts = $relativePath.Split('/')
        $directoryPath = ''
        $directory = $directoryRef
        for ($i = 0; $i -lt $parts.Length - 1; $i++) {
            $part = $parts[$i]
            $nextPath = ($directoryPath + '/' + $part).TrimStart('/')
            if (!$directories.ContainsKey($nextPath)) {
                $identity = 'directory/' + $nextPath.ToLowerInvariant()
                $suffix = (Get-Identity $identity).ToString('N')
                $id = "Dir_$suffix"
                $child = Add-WixElement $directory 'Directory' @{ Id = $id; Name = $part }
                $directories.Add($nextPath, $child)
                $folder = Add-Component "Folder_$suffix" $identity $id
                $null = Add-WixElement $folder 'CreateFolder' @{}
                $null = Add-WixElement $folder 'RemoveFolder' @{ Id = "Remove_$suffix"; On = 'uninstall' }
            }
            $directory = $directories[$nextPath]
            $directoryPath = $nextPath
        }

        $path = Join-Path $payload ($relativePath.Replace('/', '\'))
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::Copy($sourceFiles[$relativePath], $path)
        $identity = 'file/' + $relativePath.ToLowerInvariant()
        $suffix = (Get-Identity $identity).ToString('N')
        $component = Add-Component "FileComponent_$suffix" $identity $directory.GetAttribute('Id')
        $null = Add-WixElement $component 'File' @{
            Id = "File_$suffix"
            Name = $parts[-1]
            Source = '!(bindpath.payload)\' + $relativePath.Replace('/', '\')
        }
        [ordered]@{
            path = $relativePath
            size = [IO.FileInfo]::new($path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $trayVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'opssh-tray.dll')).Version
    if ($trayVersion -ne [version]"$Version.0") { throw 'The Release tray assembly version differs from the package version.' }
    $manifest = [ordered]@{ version = $Version; architecture = 'x64'; files = @($files) }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packages 'payload-manifest.json') -Encoding utf8
    $payloadSource = Join-Path $work 'Payload.wxs'
    $document.Save($payloadSource)

    $license = [IO.File]::ReadAllText((Join-Path $payload 'LICENSE'))
    $rtf = [Text.StringBuilder]::new('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ')
    foreach ($char in $license.Replace([string][char]13, '').ToCharArray()) {
        switch ($char) {
            ([char]10) { [void]$rtf.AppendLine('\par') }
            ([char]9) { [void]$rtf.Append('\tab ') }
            '\' { [void]$rtf.Append('\\') }
            '{' { [void]$rtf.Append('\{') }
            '}' { [void]$rtf.Append('\}') }
            default {
                if ([int]$char -lt 32 -or [int]$char -gt 126) {
                    $signed = ([int]$char + 32768) % 65536 - 32768
                    [void]$rtf.Append("\u$signed?")
                } else {
                    [void]$rtf.Append($char)
                }
            }
        }
    }
    [IO.File]::WriteAllText((Join-Path $work 'License.rtf'), $rtf.Append('}').ToString(), [Text.Encoding]::ASCII)

    $msiName = "$stem.msi"
    $msi = Join-Path $packages $msiName
    $wixBuild = @('build', '-acceptEula', 'wix7', '-arch', 'x64', '-pdbtype', 'none',
        '-d', "Version=$Version", '-d', "MsiUpgradeCode=$msiUpgradeCode", '-ext', "WixToolset.Util.wixext/$wixVersion")
    Push-Location -LiteralPath $repo
    try {
        Invoke-Wix ($wixBuild + @(
            (Join-Path $repo 'installer/Package.wxs'), $payloadSource,
            '-b', "payload=$payload", '-d', ('ProductCode=' + (Get-Identity "product/$Version").ToString('B')),
            '-intermediateFolder', (Join-Path $work 'msi-obj'), '-o', $msi
        ))
        Invoke-Wix ($wixBuild + @(
            (Join-Path $repo 'installer/Bundle.wxs'), '-ext', "WixToolset.BootstrapperApplications.wixext/$wixVersion",
            '-b', "work=$work", '-b', "packages=$packages", '-d', "MsiName=$msiName",
            '-intermediateFolder', (Join-Path $work 'bundle-obj'), '-o', (Join-Path $packages "$stem.exe")
        ))
    } finally {
        Pop-Location
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, (Join-Path $packages "$stem.zip"))
    [IO.Directory]::Move($packages, $OutputDirectory)
    Write-Host 'Built the Windows x64 MSI, offline EXE, and portable ZIP.'
} finally {
    # This path was created above under the explicit output parent.
    if ([IO.Path]::GetDirectoryName($work) -cne $outputParent) { throw 'Unexpected packaging work directory.' }
    Remove-Item -LiteralPath $work -Recurse -Force
}
