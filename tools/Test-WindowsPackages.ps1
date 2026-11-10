#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$VerificationDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WixPath,
    [Parameter(Mandatory)][string]$LegalDirectory
)

$ErrorActionPreference = 'Stop'
$PackageDirectory = (Get-Item -LiteralPath $PackageDirectory).FullName
$ReleaseDirectory = (Get-Item -LiteralPath $ReleaseDirectory).FullName
$VerificationDirectory = [IO.Path]::GetFullPath($VerificationDirectory, $PWD.ProviderPath)
if (Test-Path -LiteralPath $VerificationDirectory) { throw 'Choose a new package verification directory.' }
if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Use the canonical package version.' }
$stem = "opssh-$Version-windows-x64"
$msi = Join-Path $PackageDirectory "$stem.msi"
$bundle = Join-Path $PackageDirectory "$stem.exe"
$zip = Join-Path $PackageDirectory "$stem.zip"
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PackageDirectory 'payload-manifest.json') | ConvertFrom-Json
if ($manifest.version -cne $Version -or $manifest.architecture -cne 'x64') { throw 'Payload manifest identity mismatch.' }

function Assert-File([string]$Path, $Expected) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or $item.Length -ne $Expected.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected.sha256) {
        throw "Payload bytes differ for $($Expected.path)."
    }
}

function Invoke-Wix([string[]]$Arguments) {
    & $WixPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WiX $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Get-MsiRows([string]$Table, [int]$Fields) {
    $query = 'SELECT * FROM ' + [char]96 + $Table + [char]96
    $view = $database.OpenView($query)
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = for ($i = 1; $i -le $Fields; $i++) { $record.StringData($i) }
                [PSCustomObject]@{ Values = @($values) }
            } finally {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record)
            }
        }
    } finally {
        $null = $view.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
}

function Assert-Manifest($Entries, [hashtable]$Expected, [string]$Format) {
    $entriesByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        if (!$entriesByPath.TryAdd($entry.path, $entry)) { throw "Duplicate $Format manifest path." }
    }
    if ($Expected.Count -ne $entriesByPath.Count) { throw "$Format manifest file count differs from the curated Release." }
    foreach ($path in $Expected.Keys) {
        if (!$entriesByPath.ContainsKey($path)) { throw "Missing curated $Format file $path." }
        Assert-File $Expected[$path] $entriesByPath[$path]
    }
    return ,$entriesByPath
}

$expected = @{}
$expectedPortable = @{ 'opssh-tray.exe' = (Join-Path $ReleaseDirectory 'opssh-portable.exe') }
foreach ($file in Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -Force -File) {
    $name = [IO.Path]::GetRelativePath($ReleaseDirectory, $file.FullName).Replace('\', '/')
    if ($file.Extension -ine '.pdb' -and $name -ine 'opssh-portable.exe') {
        $expected.Add($name, $file.FullName)
        $expectedPortable.Add("opssh-runtime/$name", $file.FullName)
    }
}
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $LegalDirectory $name
    $expected.Add($name, $source)
    $expectedPortable.Add($name, $source)
}
$licenses = (Get-Item -LiteralPath (Join-Path $LegalDirectory 'licenses')).FullName
foreach ($file in Get-ChildItem -LiteralPath $licenses -Recurse -Force -File) {
    $path = [IO.Path]::GetRelativePath($licenses, $file.FullName).Replace('\', '/')
    $expected.Add("licenses/$path", $file.FullName)
    if ($path -ine 'wix/wix-7.0.1-source.zip') {
        $expectedPortable.Add("licenses/$path", $file.FullName)
    }
}
$files = Assert-Manifest $manifest.files $expected 'MSI'
$portableFiles = Assert-Manifest $manifest.portableFiles $expectedPortable 'ZIP'
foreach ($file in @($msi, $bundle)) {
    if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'NotSigned') {
        throw 'Expected an unsigned Windows installer.'
    }
}
$stream = [IO.File]::OpenRead($bundle)
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($pe.PEHeaders.CoffHeader.Machine -ne 'Amd64') { throw 'Expected an x64 Burn EXE.' }
    } finally {
        $pe.Dispose()
    }
} finally {
    $stream.Dispose()
}

$zipFiles = Join-Path $VerificationDirectory 'zip'
$msiFiles = Join-Path $VerificationDirectory 'msi'
$burnFiles = Join-Path $VerificationDirectory 'burn'
$burnBa = Join-Path $VerificationDirectory 'burn-ba'
$wixWork = Join-Path $VerificationDirectory 'wix'
New-Item -ItemType Directory -Path $VerificationDirectory,$wixWork | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $zipFiles)
$extracted = @(Get-ChildItem -LiteralPath $zipFiles -Recurse -Force -File)
if ($extracted.Count -ne $portableFiles.Count) { throw 'ZIP file count differs from the payload manifest.' }
foreach ($file in $extracted) {
    $path = [IO.Path]::GetRelativePath($zipFiles, $file.FullName).Replace('\', '/')
    if (!$portableFiles.ContainsKey($path)) { throw "Unexpected ZIP file $path." }
    Assert-File $file.FullName $portableFiles[$path]
}
$zipRoot = @('opssh-tray.exe', 'opssh-runtime', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses')
if (Compare-Object $zipRoot @(Get-ChildItem -LiteralPath $zipFiles -Force -Name) -CaseSensitive) {
    throw 'The portable ZIP root must expose only the launcher, the runtime directory and the legal files.'
}
$launcher = Join-Path $zipFiles 'opssh-tray.exe'
if ((Get-AuthenticodeSignature -LiteralPath $launcher).Status -ne 'NotSigned') {
    throw 'Expected an unsigned Windows launcher.'
}
$stream = [IO.File]::OpenRead($launcher)
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($pe.PEHeaders.CoffHeader.Machine -ne 'Amd64' -or $pe.PEHeaders.PEHeader.Subsystem -ne 'WindowsGui') {
            throw 'Expected a native x64 Windows GUI launcher.'
        }
    } finally {
        $pe.Dispose()
    }
} finally {
    $stream.Dispose()
}
& (Join-Path $PSScriptRoot 'Test-PortableLauncher.ps1') -LauncherPath $launcher -OutputDirectory (Join-Path $VerificationDirectory 'portable smoke')

Invoke-Wix @('msi', 'validate', '-acceptEula', 'wix7', '-ice', 'ICE38', '-ice', 'ICE64',
    '-intermediateFolder', $wixWork, $msi)
Invoke-Wix @('msi', 'decompile', '-acceptEula', 'wix7', '-data', '-x', $msiFiles,
    '-intermediateFolder', $wixWork, '-o', (Join-Path $VerificationDirectory 'msi.wixout'), $msi)

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = $installer.OpenDatabase($msi, 0)
    $summary = $database.SummaryInformation(0)
    try {
        # PID_TEMPLATE is the platform; bit 3 of PID_WORDCOUNT denotes a non-elevated package.
        if ($summary.Property(7) -cne 'x64;1033' -or ([int]$summary.Property(15) -band 8) -eq 0) {
            throw 'Expected a per-user x64 MSI.'
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($summary)
    }

    $tables = @(Get-MsiRows '_Tables' 1 | ForEach-Object { $_.Values[0] })
    foreach ($forbidden in @('Environment', 'ServiceInstall', 'ServiceControl', 'RemoveRegistry', 'DuplicateFile')) {
        if ($forbidden -in $tables) { throw "Unexpected MSI table $forbidden." }
    }
    if ('Wix4CloseApplication' -notin $tables) { throw 'The MSI is missing the running opssh guard.' }
    $guardTargets = @{ 'opssh-tray.exe' = 'OpsshTrayRunning'; 'opssh-core.exe' = 'OpsshCoreRunning' }
    foreach ($row in Get-MsiRows 'Wix4CloseApplication' 9) {
        $values = $row.Values
        if (!$guardTargets.ContainsKey($values[1]) -or $values[3] -or $values[4] -cne '0' -or
            $values[6] -cne $guardTargets[$values[1]] -or $values[7]) {
            throw 'Running process detection must never close, terminate, prompt or restart a process.'
        }
        $guardTargets.Remove($values[1])
    }
    if ($guardTargets.Count) { throw 'The MSI must check both the tray and the core.' }
    $properties = @{}
    foreach ($row in Get-MsiRows 'Property' 2) { $properties.Add($row.Values[0], $row.Values[1]) }
    if ($properties.ProductVersion -cne $Version -or $properties.ContainsKey('ALLUSERS') -or
        $properties.ContainsKey('ARPSYSTEMCOMPONENT') -or $properties.MSIRESTARTMANAGERCONTROL -cne 'Disable' -or
        $properties.REBOOT -cne 'ReallySuppress') {
        throw 'Unexpected MSI version, installation scope, maintenance, or shutdown policy.'
    }
    $conditions = @(Get-MsiRows 'LaunchCondition' 2 | ForEach-Object { $_.Values[0] })
    if ('NOT ALLUSERS' -cnotin $conditions -or 'NOT WIX_DOWNGRADE_DETECTED' -cnotin $conditions) {
        throw 'Expected MSI launch conditions for user scope and downgrade refusal.'
    }
    $actions = @{}
    $actionConditions = @{}
    foreach ($row in Get-MsiRows 'InstallExecuteSequence' 3) {
        $actions.Add($row.Values[0], [int]$row.Values[2])
        $actionConditions.Add($row.Values[0], $row.Values[1])
    }
    $uiBinding = $null
    $uiCostInitialize = 0
    foreach ($row in Get-MsiRows 'InstallUISequence' 3) {
        switch -CaseSensitive ($row.Values[0]) {
            'SetOpsshInstallDir' { $uiBinding = $row.Values }
            'CostInitialize' { $uiCostInitialize = [int]$row.Values[2] }
        }
    }
    if (!$actions.ContainsKey('SetOpsshInstallDir') -or $actionConditions.SetOpsshInstallDir -or
        $actions.SetOpsshInstallDir -le 0 -or $actions.SetOpsshInstallDir -ge $actions.CostInitialize -or
        !$uiBinding -or $uiBinding[1] -or [int]$uiBinding[2] -le 0 -or [int]$uiBinding[2] -ge $uiCostInitialize) {
        throw 'The per-user application directory must be set before costing in both MSI sequences.'
    }
    if (!$actions.ContainsKey('Wix4CloseApplications_X64') -or !$actions.ContainsKey('OpsshInUse') -or
        $actions.Wix4CloseApplications_X64 -ge $actions.OpsshInUse -or $actions.OpsshInUse -ge $actions.InstallValidate -or
        $actionConditions.Wix4CloseApplications_X64 -or $actionConditions.OpsshInUse -cne 'OpsshTrayRunning OR OpsshCoreRunning' -or
        $actions.ContainsKey('Wix4CloseApplicationsDeferred_X64') -or $actions.ContainsKey('Wix4CheckRebootRequired_X64')) {
        throw 'The running opssh guard must execute before MSI validation and changes.'
    }
    $customActions = @{}
    foreach ($row in Get-MsiRows 'CustomAction' 4) { $customActions.Add($row.Values[0], $row.Values) }
    $binding = $customActions.SetOpsshInstallDir
    if (!$binding -or $binding[1] -cne '51' -or $binding[2] -cne 'OpsshInstallDir' -or
        $binding[3] -cne '[LocalAppDataFolder]Programs\opssh\') {
        throw 'The MSI must bind its private application directory to LocalAppData\Programs\opssh.'
    }
    if ($customActions.OpsshInUse[1] -cne '19' -or $customActions.Wix4CloseApplications_X64[1] -cne '1' -or
        $customActions.Wix4CloseApplications_X64[3] -cne 'WixCloseApplications') {
        throw 'The running opssh guard must be immediate, checked, and able to abort.'
    }
    if (!$actions.ContainsKey('RemoveExistingProducts') -or
        $actions.RemoveExistingProducts -le $actions.InstallInitialize -or
        $actions.RemoveExistingProducts -ge $actions.ProcessComponents) {
        throw 'The MSI must remove an older version within the installation transaction.'
    }
    foreach ($row in Get-MsiRows 'Media' 4) {
        if (!$row.Values[3].StartsWith('#', [StringComparison]::Ordinal)) { throw 'MSI uses external cabinet media.' }
    }

    $directories = @{}
    foreach ($row in Get-MsiRows 'Directory' 3) { $directories.Add($row.Values[0], $row.Values) }
    if (!$directories.ContainsKey('OpsshInstallDir') -or
        $directories.OpsshInstallDir[1] -cne 'LocalAppDataFolder' -or $directories.OpsshInstallDir[2] -cne 'opssh') {
        throw 'The MSI application directory must have no owned parent under LocalAppData.'
    }
    $registry = @{}
    foreach ($row in Get-MsiRows 'Registry' 6) {
        $values = $row.Values
        if ($values[1] -cne '1' -or $values[2] -cne 'Software\opssh\Installer\Components') {
            throw 'Unexpected MSI registry destination.'
        }
        $registry.Add($values[0], $values)
    }
    $components = @{}
    $componentGuids = [Collections.Generic.HashSet[Guid]]::new()
    foreach ($row in Get-MsiRows 'Component' 6) {
        $values = $row.Values
        $keyPath = $registry[$values[5]]
        if (([int]$values[3] -band 260) -ne 260 -or !$keyPath -or $keyPath[5] -cne $values[0] -or
            !$componentGuids.Add([Guid]$values[1])) {
            throw 'MSI components require unique identities and their own x64 HKCU key paths.'
        }
        $components.Add($values[0], $values[2])
    }
    if ($components.Count -ne $registry.Count) { throw 'Unexpected additional MSI registry values.' }
    $shortcuts = @(Get-MsiRows 'Shortcut' 12)
    if ($shortcuts.Count -ne 1 -or $shortcuts[0].Values[1] -cne 'ProgramMenuFolder' -or
        $shortcuts[0].Values[2] -cne 'opssh' -or $shortcuts[0].Values[4] -cne '[OpsshInstallDir]opssh-tray.exe') {
        throw 'Expected one Start menu shortcut to the installed tray.'
    }

    $msiPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $payloadDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$payloadDirectories.Add('OpsshInstallDir')
    foreach ($row in Get-MsiRows 'File' 4) {
        $values = $row.Values
        $path = $values[2].Split('|')[-1]
        $id = $components[$values[1]]
        $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        while ($id -cne 'OpsshInstallDir') {
            if (!$id -or !$directories.ContainsKey($id) -or !$visited.Add($id)) {
                throw 'MSI payload file is outside the application directory.'
            }
            [void]$payloadDirectories.Add($id)
            $directory = $directories[$id]
            $path = $directory[2].Split(':')[0].Split('|')[-1] + '/' + $path
            $id = $directory[1]
        }
        if (!$msiPaths.Add($path) -or !$files.ContainsKey($path) -or [int64]$values[3] -ne $files[$path].size) {
            throw "Unexpected MSI payload row for $path."
        }
        Assert-File (Join-Path $msiFiles ('File/' + $values[0])) $files[$path]
    }
    if ($msiPaths.Count -ne $files.Count -or
        @(Get-ChildItem -LiteralPath (Join-Path $msiFiles 'File') -File -Force).Count -ne $files.Count) {
        throw 'MSI file count differs from the payload manifest.'
    }
    $createdDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in Get-MsiRows 'CreateFolder' 2) { [void]$createdDirectories.Add($row.Values[0]) }
    if (!$createdDirectories.SetEquals($payloadDirectories)) { throw 'Unexpected MSI directory ownership.' }
    $removedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in Get-MsiRows 'RemoveFile' 5) {
        $values = $row.Values
        if ($values[2] -or $values[4] -cne '2' -or !$removedDirectories.Add($values[3])) {
            throw 'MSI cleanup must target empty directories only, at uninstall.'
        }
    }
    if (!$removedDirectories.SetEquals($payloadDirectories)) { throw 'Unexpected MSI directory cleanup scope.' }
} finally {
    if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}

Invoke-Wix @('burn', 'extract', '-acceptEula', 'wix7', $bundle, '-out', $burnFiles, '-outba', $burnBa,
    '-intermediateFolder', $wixWork)
$embedded = @(Get-ChildItem -LiteralPath $burnFiles -Recurse -Force -File)
if ($embedded.Count -ne 1 -or $embedded[0].Name -cne "$stem.msi" -or
    (Get-FileHash -LiteralPath $embedded[0].FullName -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash) {
    throw 'The Burn EXE must embed exactly the standalone MSI.'
}

$burn = ([xml](Get-Content -Raw -LiteralPath (Join-Path $burnBa 'manifest.xml'))).DocumentElement
$baData = ([xml](Get-Content -Raw -LiteralPath (Join-Path $burnBa 'BootstrapperApplicationData.xml'))).DocumentElement
$search = $burn.SelectSingleNode("*[local-name()='MsiProductSearch' and @Variable='OpsshInstalledVersion']")
$downgrade = $baData.SelectSingleNode("*[local-name()='WixBalCondition' and @Condition='OpsshInstalledVersion <= WixBundleVersion']")
if (!$search -or $search.UpgradeCode -cne $properties.UpgradeCode -or $search.Type -cne 'version' -or
    !$downgrade -or $downgrade.Message -cne 'A newer version of opssh is already installed.') {
    throw 'The EXE must refuse downgrade against a newer installed MSI.'
}
$chain = @($burn.Chain.ChildNodes)
$registration = $burn.Registration
if ($chain.Count -ne 1 -or $chain[0].LocalName -cne 'MsiPackage' -or $chain[0].Scope -cne 'perUser' -or
    $chain[0].ProductCode -cne $properties.ProductCode -or $chain[0].Version -cne $Version -or
    $chain[0].Permanent -cne 'yes' -or $chain[0].Cache -cne 'remove' -or
    $registration.Scope -cne 'perUser' -or $registration.Version -cne $Version -or
    $registration.Arp.DisableModify -cne 'yes' -or $registration.Arp.DisableRemove -cne 'yes' -or
    $chain[0].SelectSingleNode("*[local-name()='MsiProperty' and @Id='ARPSYSTEMCOMPONENT']")) {
    throw 'The Burn chain must delegate visible per-user maintenance to the MSI.'
}
$payload = @($burn.Payload)
if ($payload.Count -ne 1 -or $payload[0].Packaging -cne 'embedded' -or $payload[0].DownloadUrl -or
    $burn.Container.Attached -cne 'yes' -or $burn.Container.DownloadUrl) {
    throw 'Unexpected external Burn payload.'
}
Write-Host 'Verified Windows x64 MSI scope, ICE38/ICE64, curated ZIP and MSI payload bytes, and embedded MSI identity.'
