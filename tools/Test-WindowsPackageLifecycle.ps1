#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PreviousPackageDirectory,
    [Parameter(Mandatory)][string]$PreviousVersion,
    [Parameter(Mandatory)][string]$ReportPath,
    [string]$InteropDirectory,
    [string]$ProductionVersion = $Version
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ARCH -cne 'X64') {
    throw 'Run installer lifecycle provisioning only on a disposable GitHub-hosted Windows x64 runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The hosted provisioner requires an administrative setup token.'
    }
} finally {
    $identity.Dispose()
}
foreach ($value in @($PreviousVersion, $Version)) {
    if ($value -cnotmatch '\A(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})\z' -or
        [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65534) {
        throw 'Use canonical package versions within the supported Windows range.'
    }
}
if ([version]$PreviousVersion -ge [version]$Version) { throw 'The lifecycle baseline must be older than the candidate.' }
if ($InteropDirectory -and $ProductionVersion -cnotin $PreviousVersion,$Version) {
    throw 'The installed-core smoke version must match one of the lifecycle packages.'
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath, $PWD.ProviderPath)
if (Test-Path -LiteralPath $ReportPath) { throw 'Choose a new lifecycle report file.' }
if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($ReportPath)) -PathType Container)) {
    throw 'Choose an existing lifecycle report parent directory.'
}

function Set-StagePermissions([string]$Path, [Security.Principal.SecurityIdentifier]$UserSid, [bool]$Writable) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', $inherit, $none, $allow)
        $acl.AddAccessRule($rule)
    }
    $access = if ($Writable) { 'Modify' } else { 'ReadAndExecute' }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($UserSid, $access, $inherit, $none, $allow))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Copy-Package([string]$Source, [string]$PackageVersion, [string]$Name) {
    $stem = "opssh-$PackageVersion-windows-x64"
    $root = Join-Path $stage $Name
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'msi'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'exe'))
    $hashes = @{}
    foreach ($item in @("$stem.msi", "$stem.exe", 'payload-manifest.json')) {
        $path = Join-Path $Source $item
        $sourceFile = Get-Item -LiteralPath $path
        if ($sourceFile.PSIsContainer -or ($sourceFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Expected an ordinary source package file.'
        }
        $kind = [IO.Path]::GetExtension($item).TrimStart('.')
        $relative = if ($kind -eq 'json') { $item } else { "$kind/$item" }
        $destination = Join-Path $root $relative
        $hashes[$kind] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [IO.File]::Copy($sourceFile.FullName, $destination)
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $hashes[$kind]) {
            throw 'Source package bytes changed during staging.'
        }
    }
    $msi = Join-Path $root "msi/$stem.msi"
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $database = $installer.OpenDatabase($msi, 0)
        try {
            $query = 'SELECT ' + [char]96 + 'Value' + [char]96 + ' FROM ' + [char]96 + 'Property' + [char]96 +
                ' WHERE ' + [char]96 + "Property" + [char]96 + " = 'ProductCode'"
            $view = $database.OpenView($query)
            try {
                $null = $view.Execute()
                $record = $view.Fetch()
                if (!$record) { throw 'The source MSI lacks a ProductCode.' }
                try {
                    $code = ([Guid]$record.StringData(1)).ToString('B').ToUpperInvariant()
                } finally {
                    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record)
                }
            } finally {
                $null = $view.Close()
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
            }
        } finally {
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
    [ordered]@{ version = $PackageVersion; productCode = $code; msiSha256 = $hashes.msi; exeSha256 = $hashes.exe; manifestSha256 = $hashes.json }
}

function Remove-TestUser($User) {
    $sid = $User.SID.Value
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $profile = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID = '$sid'" -OperationTimeoutSec 10
        if (!$profile.Loaded) { break }
        Start-Sleep -Milliseconds 250
    } while ($deadline.Elapsed.TotalSeconds -lt 15)
    if ($profile) {
        $profileList = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
        $profilesRoot = [Environment]::ExpandEnvironmentVariables((Get-ItemPropertyValue -LiteralPath $profileList -Name 'ProfilesDirectory'))
        $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($profilesRoot))
        $path = [IO.Path]::GetFullPath($profile.LocalPath)
        if ($profile.Special -or $profile.Loaded -or [IO.Path]::GetDirectoryName($path) -ine $root -or
            !([IO.Path]::GetFileName($path) -match ('\A' + [regex]::Escape($User.Name) + '(?:\.[A-Za-z0-9.-]+)?\z'))) {
            throw 'Refusing to remove an unverified or loaded test profile.'
        }
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to remove a redirected test profile.'
        }
        Remove-CimInstance -InputObject $profile -OperationTimeoutSec 20
        if (Get-CimInstance -ClassName Win32_UserProfile -Filter "SID = '$sid'" -OperationTimeoutSec 10) {
            throw 'The disposable user profile was not removed.'
        }
    }
    Remove-LocalUser -SID $User.SID
    if (Get-LocalUser | Where-Object { $_.SID.Value -eq $sid }) { throw 'The disposable account was not removed.' }
}

$stageParent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('CommonApplicationData'))
$nonce = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $stageParent "opssh-lifecycle-$nonce"
$user = $null
$password = [Security.SecureString]::new()
$process = $null
$staged = $false
$testFailure = $false
$cleanupFailure = $false
$failureKind = $null
$nativeErrorCode = $null
$workerExitCode = $null
$report = $null
$metadata = $null
$phase = 'create test account'
try {
    $alphabet = 'ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#%'
    foreach ($char in 'Aa2!'.ToCharArray()) { $password.AppendChar($char) }
    for ($i = 0; $i -lt 32; $i++) {
        $password.AppendChar($alphabet[[Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)])
    }
    $password.MakeReadOnly()
    $name = 'opssh-ci-' + $nonce.Substring(0, 10)
    $user = New-LocalUser -Name $name -Password $password -AccountExpires (Get-Date).AddHours(1) -UserMayNotChangePassword
    $usersSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    if (!(Get-LocalGroupMember -SID $usersSid | Where-Object { $_.SID -eq $user.SID })) {
        Add-LocalGroupMember -SID $usersSid -Member $user
    }

    $phase = 'stage packages and read-only inputs'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $staged = $true
    Set-StagePermissions $stage $user.SID $false
    $resultDirectory = Join-Path $stage 'results'
    [void][IO.Directory]::CreateDirectory($resultDirectory)
    Set-StagePermissions $resultDirectory $user.SID $true
    $metadata = [ordered]@{
        previous = Copy-Package $PreviousPackageDirectory $PreviousVersion 'previous'
        candidate = Copy-Package $PackageDirectory $Version 'candidate'
    }
    if ($metadata.previous.productCode -ceq $metadata.candidate.productCode) { throw 'Expected different product codes.' }
    $expectedSteps = 22
    if ($InteropDirectory) {
        $phase = 'stage the production core test driver'
        $interop = Join-Path $stage 'interop'
        [void][IO.Directory]::CreateDirectory($interop)
        $driverFiles = foreach ($filename in @('opssh-core.dll','opssh-core.deps.json','opssh-core.runtimeconfig.json')) {
            $source = Get-Item -LiteralPath (Join-Path $InteropDirectory $filename)
            if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Expected an ordinary interop test driver file.'
            }
            [IO.File]::Copy($source.FullName, (Join-Path $interop $filename))
            [ordered]@{ name = $filename; sha256 = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash }
        }
        $metadata.interop = @($driverFiles)
        $metadata.productionVersion = $ProductionVersion
        $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $expectedSteps++
    }
    $metadata | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'inputs.json') -Encoding utf8
    $worker = Join-Path $stage 'Test-WindowsPackageLifecycleUser.ps1'
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Test-WindowsPackageLifecycleUser.ps1'), $worker)
    $stubs = Join-Path $stage 'process-stubs'
    [void][IO.Directory]::CreateDirectory($stubs)
    foreach ($imageName in @('opssh-tray.exe', 'opssh-core.exe')) {
        [IO.File]::Copy((Join-Path ([Environment]::SystemDirectory) 'ping.exe'), (Join-Path $stubs $imageName))
    }

    $phase = 'run standard-user lifecycle cases'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $stage
    $start.UserName = $user.Name
    $start.Domain = $env:COMPUTERNAME
    $start.Password = $password
    $start.LoadUserProfile = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$worker,'-ExpectedSid',$user.SID.Value,
        '-ResultDirectory',$resultDirectory)) { $start.ArgumentList.Add($argument) }
    if ($InteropDirectory) {
        $start.ArgumentList.Add('-DotnetPath')
        $start.ArgumentList.Add($dotnet)
    }
    $process = [Diagnostics.Process]::Start($start)
    # Discard output as bytes to avoid retaining unexpected host details in memory or CI logs.
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderr = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    if (!$process.WaitForExit(2100000) -or ![Threading.Tasks.Task]::WaitAll(@($stdout, $stderr), 10000)) {
        throw 'The standard-user lifecycle worker exceeded its deadline.'
    }
    $workerExitCode = $process.ExitCode
    $phase = 'read standard-user lifecycle report'
    $childReport = Join-Path $resultDirectory 'lifecycle-report.json'
    if (!(Test-Path -LiteralPath $childReport -PathType Leaf)) {
        throw 'The standard-user lifecycle worker did not write a report.'
    }
    $report = Get-Content -Raw -LiteralPath $childReport | ConvertFrom-Json
    $phase = 'verify standard-user lifecycle results'
    if ($workerExitCode -ne 0 -or !$report.passed -or !$report.verifiedStandardUser -or
        !$report.installerCleanupPassed -or $report.phase -cne 'passed' -or @($report.steps).Count -ne $expectedSteps) {
        $testFailure = $true
    }
} catch {
    $testFailure = $true
    $failure = $_.Exception.GetBaseException()
    $failureKind = $failure.GetType().Name
    if ($failure -is [ComponentModel.Win32Exception]) { $nativeErrorCode = $failure.NativeErrorCode }
} finally {
    $password.Dispose()
    if ($process) {
        try {
            if (!$process.HasExited) {
                $process.Kill($true)
                if (!$process.WaitForExit(10000)) { throw 'Could not stop the owned worker.' }
            }
        } catch {
            $cleanupFailure = $true
        } finally {
            $process.Dispose()
        }
    }
    if ($user) {
        try {
            # Disable first so a retained account cannot log in if profile cleanup fails.
            Disable-LocalUser -SID $user.SID
            Remove-TestUser $user
        } catch {
            $cleanupFailure = $true
        }
    }
    if ($staged) {
        try {
            $resolved = Get-Item -LiteralPath $stage
            if ([IO.Path]::GetDirectoryName($resolved.FullName) -ine $stageParent -or
                $resolved.Name -cne "opssh-lifecycle-$nonce" -or ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Refusing to remove an unverified staging directory.'
            }
            Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
            if (Test-Path -LiteralPath $stage) { throw 'The lifecycle staging directory remains.' }
        } catch {
            $cleanupFailure = $true
        }
    }
}

[ordered]@{
    passed = !$testFailure -and !$cleanupFailure
    phase = $phase
    accountCleanupPassed = !$cleanupFailure
    failureKind = $failureKind
    nativeErrorCode = $nativeErrorCode
    workerExitCode = $workerExitCode
    inputs = $metadata
    lifecycle = $report
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
if ($cleanupFailure) { throw 'Disposable lifecycle cleanup failed. Discard the hosted runner.' }
if ($testFailure) { throw "Windows package lifecycle verification failed during $phase." }
Write-Host 'Verified Windows package lifecycle under a disposable standard user and removed the account and profile.'
