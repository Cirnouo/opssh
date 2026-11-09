#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedSid,
    [Parameter(Mandatory)][string]$ResultDirectory,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$steps = [Collections.Generic.List[object]]::new()
$stage = 'verify standard-user token'
$installer = $null
$verifiedToken = $false
$startedCases = $false
$failed = $false
$cleanupFailed = $false
$failureKind = $null
$failureCheck = $null
$cleanupFailureCheck = $null
$checkpoint = $null
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
$logDirectory = Join-Path $ResultDirectory 'installer-logs'
$arpPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
$installerRegistry = 'HKCU:\Software\opssh\Installer\Components'
$guardMessage = 'Quit opssh before installing, repairing, or removing it.'

function Get-Digest([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Start-OwnedProcess([string]$Path, [string[]]$Arguments, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = $Environment[$name] }
    [Diagnostics.Process]::Start($start)
}

function Stop-OwnedProcess([Diagnostics.Process]$Process) {
    try {
        if (!$Process.HasExited) {
            $Process.Kill($true)
            if (!$Process.WaitForExit(10000)) { throw 'A lifecycle process did not exit.' }
        }
    } finally {
        $Process.Dispose()
    }
}

function Invoke-Setup($Package, [string]$EntryPoint, [string]$Operation, [string]$Expected) {
    $log = Join-Path $logDirectory "$($steps.Count).log"
    if ($EntryPoint -eq 'exe') {
        $path = $Package.Exe
        $arguments = @('-quiet', '-norestart', '-log', $log)
    } else {
        $path = $msiexec
        $command = switch ($Operation) {
            install { @('/i', $Package.Msi) }
            repair { @('/fav', $Package.Msi) }
            uninstall { @('/x', $Package.ProductCode) }
        }
        $arguments = @($command) + @('/qn', '/norestart', '/l*v', $log)
    }
    $process = Start-OwnedProcess $path $arguments
    try {
        if (!$process.WaitForExit(180000)) { throw 'Installer exceeded its deadline.' }
        $code = $process.ExitCode
    } finally {
        Stop-OwnedProcess $process
    }
    $step = [ordered]@{ phase = $script:stage; entryPoint = $EntryPoint; operation = $Operation; exitCode = $code }
    $steps.Add($step)
    $win32Code = $code -band 0xffff
    if ($win32Code -eq 1625) {
        # Report only the effective values from Windows Installer, never its private log text.
        $stem = $steps.Count - 1
        $policyLogs = @(Get-ChildItem -LiteralPath $logDirectory -File -Filter "$stem*.log" |
            Where-Object { $_.BaseName -eq "$stem" -or $_.BaseName.StartsWith("${stem}_") })
        $step.policy = [ordered]@{}
        foreach ($name in @('DisableMsi', 'DisableUserInstalls')) {
            $step.policy[$name] = @($policyLogs | ForEach-Object {
                Select-String -LiteralPath $_.FullName -Pattern "Machine policy value '$name' is ([0-9]{1,2})\s*$" |
                    ForEach-Object { [int]$_.Matches[0].Groups[1].Value }
            } | Sort-Object -Unique)
        }
    }
    if ($Expected -eq 'success') {
        if ($code -ne 0) { throw 'Installer did not return success without a restart.' }
        return
    }
    if ($Expected -eq 'guard') {
        $message = [regex]::Escape($guardMessage)
        $expectedCode = $win32Code -eq 1603
    } else {
        $message = 'A newer version of opssh is already installed\.'
        $expectedCode = $win32Code -in 1603,1638 -or ($EntryPoint -eq 'exe' -and $code -eq [int]0x81F40001)
    }
    # Burn's per-MSI logs share the chosen filename stem.
    $logs = @(Get-ChildItem -LiteralPath $logDirectory -File -Filter "$($steps.Count - 1)*.log" |
        Where-Object { $_.BaseName -eq ($steps.Count - 1).ToString() -or $_.BaseName.StartsWith("$($steps.Count - 1)_") })
    if (!$expectedCode -or !$logs -or !(Select-String -LiteralPath $logs.FullName -Pattern $message -Quiet)) {
        throw 'Installer refusal did not establish the expected reason.'
    }
}

function Read-Package($Details, [string]$Name) {
    if ($Details.version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -or $Details.productCode -cnotmatch
        '\A\{[0-9A-Fa-f-]{36}\}\z') {
        throw 'Invalid staged package identity.'
    }
    $folder = Join-Path $PSScriptRoot $Name
    $stem = "opssh-$($Details.version)-windows-x64"
    $msi = Join-Path $folder "msi/$stem.msi"
    $exe = Join-Path $folder "exe/$stem.exe"
    $manifestPath = Join-Path $folder 'payload-manifest.json'
    if ((Get-Digest $msi) -cne $Details.msiSha256 -or (Get-Digest $exe) -cne $Details.exeSha256 -or
        (Get-Digest $manifestPath) -cne $Details.manifestSha256 -or
        @(Get-ChildItem -LiteralPath (Split-Path -Parent $exe) -Force).Count -ne 1) {
        throw 'Staged package inputs changed or the EXE is not isolated.'
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -cne $Details.version -or $manifest.architecture -cne 'x64') {
        throw 'Staged payload manifest identity mismatch.'
    }
    [PSCustomObject]@{ Msi = $msi; Exe = $exe; Version = $Details.version; ProductCode = $Details.productCode; Files = $manifest.files }
}

function Get-Registration([string]$Hive) {
    $registry = Join-Path $Hive $arpPath
    if (!(Test-Path -LiteralPath $registry)) { return }
    foreach ($key in Get-ChildItem -LiteralPath $registry) {
        $entry = Get-ItemProperty -LiteralPath $key.PSPath
        if ($entry.PSChildName -in @($previous.ProductCode, $candidate.ProductCode) -or $entry.DisplayName -eq 'opssh' -or
            $entry.BundleUpgradeCode -contains '{93B28357-F0AD-4D07-8959-F18BFDE560E9}') {
            $entry
        }
    }
}

function Get-MsiInstallations($Package, [switch]$VerifyIdentity) {
    # Windows Installer's product context identifies ownership independently of
    # the hive that contains the Add/Remove Programs metadata.
    # MSIINSTALLCONTEXT_ALL queries machine and current-user contexts with an empty SID.
    $products = $installer.ProductsEx($Package.ProductCode, '', 7)
    $getProperty = [Reflection.BindingFlags]'Public, Instance, GetProperty'
    try {
        foreach ($product in $products) {
            try {
                $type = $product.GetType()
                $code = $type.InvokeMember('ProductCode', $getProperty, $null, $product, @())
                if ($code -ine $Package.ProductCode) { throw 'Unexpected MSI product identity.' }
                $context = $type.InvokeMember('Context', $getProperty, $null, $product, @())
                $sid = $type.InvokeMember('UserSID', $getProperty, $null, $product, @())
                $installed = $product.InstallProperty('State') -ceq '5'
                [PSCustomObject]@{
                    owned = [int]$context -eq 2 -and $sid -ceq $ExpectedSid
                    installed = $installed
                    expectedIdentity = $VerifyIdentity -and $installed -and $product.InstallProperty('InstalledProductName') -ceq 'opssh' -and
                        $product.InstallProperty('VersionString') -ceq $Package.Version
                }
            } finally {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($product)
            }
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($products)
    }
}

function Get-ProtectedState {
    $keys = @(
        'HKCU:\Environment', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
    )
    $state = [ordered]@{}
    foreach ($name in $keys) {
        $values = [ordered]@{}
        $key = Get-Item -LiteralPath $name -ErrorAction SilentlyContinue
        if ($key) {
            try {
                foreach ($valueName in $key.GetValueNames() | Sort-Object) {
                    $values[$valueName] = $key.GetValue($valueName, $null, 'DoNotExpandEnvironmentNames')
                }
            } finally {
                $key.Dispose()
            }
        }
        $state[$name] = $values
    }
    foreach ($folder in @($startup, $commonStartup)) {
        $state[$folder] = @(Get-ChildItem -LiteralPath $folder -Force -ErrorAction SilentlyContinue |
            Sort-Object Name | ForEach-Object {
                if ($_.PSIsContainer) { $_.Name + '/' } else { $_.Name + ':' + (Get-Digest $_.FullName) }
            })
    }
    $state['services'] = @(Get-Service | Where-Object { $_.Name -match 'opssh|ssh-agent' } |
        Sort-Object Name | ForEach-Object { "$($_.Name) $($_.StartType)" })
    $state | ConvertTo-Json -Depth 5 -Compress
}

function Assert-ProtectedState {
    $script:checkpoint = 'shared Programs directory'
    if (!(Test-Path -LiteralPath $sharedPrograms -PathType Container) -or
        @(Get-ChildItem -LiteralPath $sharedPrograms -Force | Where-Object { $_.FullName -ine $installDirectory }).Count) {
        throw 'Installer changed the shared Programs directory outside the application directory.'
    }
    $script:checkpoint = 'settings and system state'
    if ((Get-Digest $sentinel) -cne $sentinelHash -or (Get-ProtectedState) -cne $protectedState) {
        throw 'Installer changed the preserved settings, PATH, startup, or SSH service state.'
    }
    $script:checkpoint = $null
}

function Assert-Uninstalled {
    $script:checkpoint = 'removed files and MSI registrations'
    if ((Test-Path -LiteralPath $installDirectory) -or (Test-Path -LiteralPath $shortcut) -or
        (Test-Path -LiteralPath $machineShortcut) -or @(Get-Registration 'HKCU:\').Count -or @(Get-Registration 'HKLM:\').Count -or
        @(Get-MsiInstallations $previous).Count -or @(Get-MsiInstallations $candidate).Count) {
        throw 'Installed files, shortcut, or registration remain.'
    }
    $script:checkpoint = 'removed MSI components'
    if ((Test-Path -LiteralPath $installerRegistry) -and @(Get-ItemProperty -LiteralPath $installerRegistry |
        ForEach-Object { $_.PSObject.Properties.Name | Where-Object { $_ -notin 'PSPath','PSParentPath','PSChildName','PSDrive','PSProvider' } }).Count) {
        throw 'An owned MSI component registration remains.'
    }
    $script:checkpoint = $null
}

function Assert-Installed($Package, $Other) {
    $script:checkpoint = 'per-user MSI inventory'
    $products = @(Get-MsiInstallations $Package -VerifyIdentity)
    if ($products.Count -ne 1 -or !$products[0].owned -or !$products[0].expectedIdentity -or
        @(Get-MsiInstallations $Other).Count) {
        throw 'Expected one installed MSI of this version in the current user context.'
    }
    $script:checkpoint = 'uninstall entries and Burn ownership'
    $registrations = @(Get-Registration 'HKCU:\'; Get-Registration 'HKLM:\')
    if ($registrations.Count -ne 1 -or $registrations[0].PSChildName -ine $Package.ProductCode -or
        $registrations[0].SystemComponent -eq 1 -or $registrations[0].WindowsInstaller -ne 1 -or
        $registrations[0].DisplayName -cne 'opssh' -or $registrations[0].DisplayVersion -cne $Package.Version) {
        throw 'Expected exactly one visible uninstall entry for the per-user MSI.'
    }
    $script:checkpoint = 'per-user Start menu shortcut'
    if (!(Test-Path -LiteralPath $shortcut -PathType Leaf) -or (Test-Path -LiteralPath $machineShortcut)) {
        throw 'Expected only a per-user Start menu shortcut.'
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcut)
        try {
            if ($link.TargetPath -ine (Join-Path $installDirectory 'opssh-tray.exe')) { throw 'The Start menu shortcut target differs.' }
        } finally {
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($link)
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    $script:checkpoint = 'installed payload bytes'
    $actual = @(Get-ChildItem -LiteralPath $installDirectory -Recurse -Force -File)
    if ($actual.Count -ne $Package.Files.Count) { throw 'Installed file count differs from the payload.' }
    foreach ($file in $Package.Files) {
        $path = Join-Path $installDirectory $file.path
        if ((Get-Digest $path) -ine $file.sha256 -or [IO.FileInfo]::new($path).Length -ne $file.size) {
            throw 'Installed bytes differ from the package manifest.'
        }
    }
    Assert-ProtectedState
}

function Assert-RunningGuard([string]$Name, $Package, [string]$EntryPoint, [string]$Operation) {
    $stub = Join-Path $PSScriptRoot "process-stubs/$Name.exe"
    $process = Start-OwnedProcess $stub @('-n', '600', '127.0.0.1')
    try {
        if ($process.HasExited -or $process.ProcessName -cne $Name) { throw 'The named process test fixture did not start.' }
        Invoke-Setup $Package $EntryPoint $Operation 'guard'
        if ($process.HasExited) { throw 'The installer stopped the running process.' }
    } finally {
        Stop-OwnedProcess $process
    }
}

function Assert-InstalledCore($Package, $DriverFiles) {
    $interop = Join-Path $ResultDirectory 'core-smoke'
    $isolatedLocalData = Join-Path $interop 'local-data'
    [void][IO.Directory]::CreateDirectory($isolatedLocalData)
    foreach ($driver in $DriverFiles) {
        $source = Join-Path $PSScriptRoot "interop/$($driver.name)"
        $target = Join-Path $interop $driver.name
        [IO.File]::Copy($source, $target)
        if ((Get-Digest $target) -cne $driver.sha256) { throw 'The staged production core test driver changed.' }
    }
    $core = Join-Path $interop 'opssh-core.exe'
    [IO.File]::Copy((Join-Path $installDirectory 'opssh-core.exe'), $core)
    if ((Get-Digest $core) -ine ($Package.Files | Where-Object path -ceq 'opssh-core.exe').sha256) {
        throw 'The production core copy differs from the installed artifact.'
    }
    $process = Start-OwnedProcess $DotnetPath @((Join-Path $interop 'opssh-core.dll'), '--production-version', $Package.Version) @{
        LOCALAPPDATA = $isolatedLocalData
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    }
    try {
        if (!$process.WaitForExit(30000)) { throw 'Production core smoke exceeded its deadline.' }
        $code = $process.ExitCode
    } finally {
        Stop-OwnedProcess $process
    }
    $steps.Add([ordered]@{ phase = $script:stage; entryPoint = 'installed-core'; operation = 'smoke'; exitCode = $code })
    if ($code -ne 0) { throw 'Production core did not pass the isolated hello, status, and shutdown smoke.' }
    Assert-ProtectedState
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $usersSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
        $groups = & (Join-Path ([Environment]::SystemDirectory) 'whoami.exe') /groups /fo csv /nh
        if ($LASTEXITCODE -ne 0 -or $identity.User.Value -cne $ExpectedSid -or
            $identity.Groups.Contains($adminSid) -or !$identity.Groups.Contains($usersSid) -or
            ($groups -join ' ') -notmatch '\bS-1-16-8192\b' -or ($groups -join ' ') -match '\bS-1-5-32-544\b') {
            throw 'Expected the disposable standard-user medium integrity token.'
        }
    } finally {
        $identity.Dispose()
    }
    $verifiedToken = $true

    $stage = 'verify staged packages and fresh profile'
    [void][IO.Directory]::CreateDirectory($logDirectory)
    $metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'inputs.json') -Raw | ConvertFrom-Json
    $previous = Read-Package $metadata.previous 'previous'
    $candidate = Read-Package $metadata.candidate 'candidate'
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $programs = [Environment]::GetFolderPath('Programs', 'DoNotVerify')
    $commonPrograms = [Environment]::GetFolderPath('CommonPrograms', 'DoNotVerify')
    $startup = [Environment]::GetFolderPath('Startup', 'DoNotVerify')
    $commonStartup = [Environment]::GetFolderPath('CommonStartup', 'DoNotVerify')
    if (!$local -or !$programs -or !$commonPrograms -or !$startup -or !$commonStartup) { throw 'User profile is unavailable.' }
    $sharedPrograms = Join-Path $local 'Programs'
    $installDirectory = Join-Path $sharedPrograms 'opssh'
    $shortcut = Join-Path $programs 'opssh.lnk'
    $machineShortcut = Join-Path $commonPrograms 'opssh.lnk'
    $settings = Join-Path $local 'opssh'
    if ((Test-Path -LiteralPath $settings) -or @(Get-Process opssh-tray,opssh-core -ErrorAction SilentlyContinue).Count) {
        throw 'Lifecycle verification requires an unused opssh profile and no running application.'
    }
    Assert-Uninstalled
    [void][IO.Directory]::CreateDirectory($sharedPrograms)
    if (@(Get-ChildItem -LiteralPath $sharedPrograms -Force).Count) {
        throw 'Lifecycle verification requires an empty preexisting Programs directory.'
    }
    [void][IO.Directory]::CreateDirectory($settings)
    $sentinel = Join-Path $settings 'installer-verification.txt'
    [IO.File]::WriteAllText($sentinel, 'opssh installer verification sentinel')
    $sentinelHash = Get-Digest $sentinel
    $protectedState = Get-ProtectedState
    $startedCases = $true

    $stage = 'running tray blocks initial MSI'
    Assert-RunningGuard 'opssh-tray' $previous 'msi' 'install'
    Assert-Uninstalled
    Assert-ProtectedState

    foreach ($scenario in @('msi-msi', 'exe-exe', 'msi-exe', 'exe-msi')) {
        $entry, $upgrade = $scenario.Split('-')
        $stage = "$scenario prior install"
        Invoke-Setup $previous $entry 'install' 'success'
        Assert-Installed $previous $candidate
        if ($scenario -eq 'msi-msi' -and $DotnetPath -and $metadata.productionVersion -ceq $previous.Version) {
            $stage = 'isolated installed production core smoke'
            Assert-InstalledCore $previous $metadata.interop
        }

        if ($entry -eq $upgrade) {
            $missing = Join-Path $installDirectory 'opssh-tray.pri'
            Remove-Item -LiteralPath $missing
            if ($scenario -eq 'msi-msi') {
                $stage = 'running core blocks MSI repair'
                Assert-RunningGuard 'opssh-core' $previous 'msi' 'repair'
                if (Test-Path -LiteralPath $missing) { throw 'The refused repair modified the payload.' }
                Assert-ProtectedState
            }
            $stage = "$scenario MSI repair"
            Invoke-Setup $previous 'msi' 'repair' 'success'
            Assert-Installed $previous $candidate
        }
        if ($scenario -eq 'exe-exe') {
            $stage = 'running core blocks EXE upgrade'
            Assert-RunningGuard 'opssh-core' $candidate 'exe' 'install'
            Assert-Installed $previous $candidate
        }

        $stage = "$scenario major upgrade"
        Invoke-Setup $candidate $upgrade 'install' 'success'
        Assert-Installed $candidate $previous
        if ($scenario -eq 'msi-msi' -and $DotnetPath -and $metadata.productionVersion -ceq $candidate.Version) {
            $stage = 'isolated installed production core smoke'
            Assert-InstalledCore $candidate $metadata.interop
        }

        $stage = "$scenario downgrade refusal"
        Invoke-Setup $previous $entry 'install' 'downgrade'
        Assert-Installed $candidate $previous

        if ($scenario -eq 'msi-msi') {
            $stage = 'running tray blocks MSI removal'
            Assert-RunningGuard 'opssh-tray' $candidate 'msi' 'uninstall'
            Assert-Installed $candidate $previous
        }

        $stage = "$scenario MSI-owned uninstall"
        Invoke-Setup $candidate 'msi' 'uninstall' 'success'
        Assert-Uninstalled
        Assert-ProtectedState
    }
    $stage = 'passed'
} catch {
    # Keep installer log paths and raw host errors inside the disposable job.
    $failed = $true
    $failureKind = $_.Exception.GetType().Name
    $failureCheck = $checkpoint
} finally {
    if ($failed -and $startedCases) {
        $failedStage = $stage
        $stage = 'remove any remaining owned lifecycle MSI'
        try {
            $checkpoint = 'owned MSI cleanup inventory'
            foreach ($package in @($candidate, $previous)) {
                $owned = @(Get-MsiInstallations $package)
                if (!$owned.Count) { continue }
                if ($owned.Count -ne 1 -or !$owned[0].owned -or !$owned[0].installed) {
                    throw 'The remaining product is not a verified per-user lifecycle MSI.'
                }
                $checkpoint = 'owned MSI cleanup transaction'
                Invoke-Setup $package 'msi' 'uninstall' 'success'
                $checkpoint = 'owned MSI cleanup inventory'
            }
            Assert-Uninstalled
        } catch {
            $cleanupFailed = $true
            $cleanupFailureCheck = $checkpoint
        } finally {
            $stage = $failedStage
        }
    }
    if ($installer) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
    $report = [ordered]@{
        passed = !$failed
        phase = $stage
        verifiedStandardUser = $verifiedToken
        installerCleanupPassed = !$cleanupFailed
        failureKind = $failureKind
        failureCheck = $failureCheck
        cleanupFailureCheck = $cleanupFailureCheck
        steps = $steps
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultDirectory 'lifecycle-report.json') -Encoding utf8
}
if ($failed) { exit 1 }
