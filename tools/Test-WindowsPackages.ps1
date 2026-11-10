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
$expectedArtifacts = @("$stem.msi", 'payload-manifest.json')
if (Compare-Object $expectedArtifacts @(Get-ChildItem -LiteralPath $PackageDirectory -Force -Name) -CaseSensitive) {
    throw 'Expected only the Windows x64 MSI and its payload manifest.'
}
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

function Assert-Manifest($Entries, [hashtable]$Expected) {
    $entriesByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        if (!$entriesByPath.TryAdd($entry.path, $entry)) { throw 'Duplicate MSI manifest path.' }
    }
    if ($Expected.Count -ne $entriesByPath.Count) { throw 'MSI manifest file count differs from the curated Release.' }
    foreach ($path in $Expected.Keys) {
        if (!$entriesByPath.ContainsKey($path)) { throw "Missing curated MSI file $path." }
        Assert-File $Expected[$path] $entriesByPath[$path]
    }
    return ,$entriesByPath
}

$expected = @{}
foreach ($file in Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -Force -File) {
    if ($file.Name -ieq 'opssh-portable.exe') { throw 'Use a fresh Release directory without the retired launcher.' }
    if ($file.Extension -ieq '.pdb') { continue }
    $name = [IO.Path]::GetRelativePath($ReleaseDirectory, $file.FullName).Replace('\', '/')
    $expected.Add($name, $file.FullName)
}
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $expected.Add($name, (Join-Path $LegalDirectory $name))
}
$licenses = (Get-Item -LiteralPath (Join-Path $LegalDirectory 'licenses')).FullName
foreach ($file in Get-ChildItem -LiteralPath $licenses -Recurse -Force -File) {
    $path = [IO.Path]::GetRelativePath($licenses, $file.FullName).Replace('\', '/')
    $expected.Add("licenses/$path", $file.FullName)
}
$files = Assert-Manifest $manifest.files $expected
if (!$files.ContainsKey('licenses/wix/wix-7.0.1-source.zip')) { throw 'The MSI must include WiX corresponding source.' }
if ((Get-AuthenticodeSignature -LiteralPath $msi).Status -ne 'NotSigned') { throw 'Expected an unsigned Windows MSI.' }

$msiFiles = Join-Path $VerificationDirectory 'msi'
$wixWork = Join-Path $VerificationDirectory 'wix'
New-Item -ItemType Directory -Path $VerificationDirectory,$wixWork | Out-Null

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
        # PID_TEMPLATE identifies the platform, PID_PAGECOUNT the minimum engine version.
        # Bit 3 of PID_WORDCOUNT denotes a non-elevated package.
        if ($summary.Property(7) -cne 'x64;1033' -or [int]$summary.Property(14) -ne 500 -or
            ([int]$summary.Property(15) -band 8) -eq 0) {
            throw 'Expected a per-user x64 MSI requiring Windows Installer 5.0.'
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
    if ($properties.ProductVersion -cne $Version -or $properties.UpgradeCode -cne '{51789FE4-B851-4225-8E02-B09E6E2C4696}' -or
        $properties.ContainsKey('ALLUSERS') -or
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
    $uiActions = @{}
    foreach ($row in Get-MsiRows 'InstallUISequence' 3) { $uiActions.Add($row.Values[0], $row.Values) }
    $uiBinding = $uiActions.SetOpsshInstallDir
    if (!$actions.ContainsKey('SetOpsshInstallDir') -or $actionConditions.SetOpsshInstallDir -or
        $actions.SetOpsshInstallDir -le 0 -or $actions.SetOpsshInstallDir -ge $actions.CostInitialize -or
        !$uiBinding -or $uiBinding[1] -or [int]$uiBinding[2] -le 0 -or
        [int]$uiBinding[2] -ge [int]$uiActions.CostInitialize[2]) {
        throw 'The per-user application directory must be set before costing in both MSI sequences.'
    }
    $finish = $uiActions.OpsshComplete
    if (!$finish -or $finish[2] -cne '-1' -or $actions.ContainsKey('OpsshComplete') -or
        $finish[1] -cne 'UILevel = 5 AND NOT Installed AND NOT WIX_UPGRADE_DETECTED AND NOT MsiRunningElevated' -or
        !$uiActions.FindRelatedProducts -or [int]$uiActions.FindRelatedProducts[2] -ge [int]$uiActions.ExecuteAction[2]) {
        throw 'The options dialog must run only after a fresh, interactive, non-elevated installation.'
    }
    $optionsDialog = @(Get-MsiRows 'Dialog' 10 | Where-Object { $_.Values[0] -ceq 'OpsshComplete' })
    if ($optionsDialog.Count -ne 1 -or $optionsDialog[0].Values[8] -cne 'Finish' -or
        $optionsDialog[0].Values[9] -cne 'Skip') {
        throw 'The MSI options dialog must let users apply or skip onboarding.'
    }
    $checkboxes = @{}
    foreach ($row in Get-MsiRows 'CheckBox' 2) { $checkboxes.Add($row.Values[0], $row.Values[1]) }
    $optionControls = @{ LoginStartup = 'OpsshLoginStartup'; DefaultAgent = 'OpsshDefaultAgent' }
    foreach ($row in Get-MsiRows 'Control' 9 | Where-Object { $_.Values[0] -ceq 'OpsshComplete' -and $_.Values[2] -ceq 'CheckBox' }) {
        $control = $row.Values[1]
        $property = $row.Values[8]
        if ($optionControls[$control] -cne $property -or $checkboxes[$property] -cne '1' -or $properties[$property] -cne '1') {
            throw 'The MSI options must bind to checked-by-default private properties.'
        }
        $optionControls.Remove($control)
    }
    if ($optionControls.Count) { throw 'The MSI is missing a Finish dialog option.' }
    $agentConditions = @(Get-MsiRows 'ControlCondition' 4 |
        Where-Object { $_.Values[0] -ceq 'OpsshComplete' -and $_.Values[1] -ceq 'DefaultAgent' } |
        ForEach-Object { $_.Values[2..3] -join ': ' })
    if (Compare-Object @('Enable: OpsshLoginStartup = 1', 'Disable: NOT OpsshLoginStartup') $agentConditions -CaseSensitive) {
        throw 'The default-agent option must depend on login startup.'
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
    $trayFileId = $null
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
        if ($path -ceq 'opssh-tray.exe') { $trayFileId = $values[0] }
        Assert-File (Join-Path $msiFiles ('File/' + $values[0])) $files[$path]
    }
    if ($msiPaths.Count -ne $files.Count -or
        @(Get-ChildItem -LiteralPath (Join-Path $msiFiles 'File') -File -Force).Count -ne $files.Count) {
        throw 'MSI file count differs from the payload manifest.'
    }
    $controlEvents = @(Get-MsiRows 'ControlEvent' 6)
    $skipEvents = @($controlEvents | Where-Object { $_.Values[0] -ceq 'OpsshComplete' -and $_.Values[1] -ceq 'Skip' })
    if ($skipEvents.Count -ne 1 -or ($skipEvents[0].Values[2..4] -join '|') -cne 'EndDialog|Return|1') {
        throw 'Skipping the MSI options must close the dialog without launching the tray.'
    }
    $finishEvents = @($controlEvents | Where-Object {
        $_.Values[2] -ceq 'MsiLaunchApp' -or ($_.Values[0] -ceq 'OpsshComplete' -and $_.Values[1] -ceq 'Finish')
    } | Sort-Object { [int]$_.Values[5] })
    $expectedEvents = @(
        "MsiLaunchApp|[#$trayFileId] --install-login-default|NOT MsiRunningElevated AND OpsshLoginStartup = 1 AND OpsshDefaultAgent = 1",
        "MsiLaunchApp|[#$trayFileId] --install-login|NOT MsiRunningElevated AND OpsshLoginStartup = 1 AND NOT OpsshDefaultAgent",
        'EndDialog|Return|1'
    )
    if (!$trayFileId -or $finishEvents.Count -ne $expectedEvents.Count) {
        throw 'The MSI Finish dialog must offer only the two installed tray launch paths and close.'
    }
    for ($i = 0; $i -lt $expectedEvents.Count; $i++) {
        $event = $finishEvents[$i].Values
        if ($event[0] -cne 'OpsshComplete' -or $event[1] -cne 'Finish' -or [int]$event[5] -ne ($i + 1) -or
            ($event[2..4] -join '|') -cne $expectedEvents[$i]) {
            throw 'The MSI Finish button must dispatch fixed commands to the installed tray and always close.'
        }
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

Write-Host 'Verified Windows x64 MSI scope, ICE38/ICE64, Finish dialog bindings, and curated payload bytes.'
