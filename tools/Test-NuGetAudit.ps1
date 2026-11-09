[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$config = Join-Path $repo '.github/nuget-audit.config'
$required = @(
    'tray/Opssh.Tray/Opssh.Tray.csproj',
    'tray/Opssh.Tray.Tests/Opssh.Tray.Tests.csproj'
)
$usedLocks = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

Push-Location -LiteralPath $repo
try {
    $projects = @(& git -c core.quotePath=false ls-files -- ':(icase,glob)**/*.csproj' ':(icase,glob)**/*.fsproj' ':(icase,glob)**/*.vbproj')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked .NET projects.' }
    foreach ($project in $required) {
        if ($project -cnotin $projects) { throw "Required .NET audit project is absent: $project" }
    }

    foreach ($project in $projects) {
        $file = Get-Item -LiteralPath $project
        $lock = Join-Path $file.DirectoryName "packages.$($file.BaseName.Replace(' ', '_')).lock.json"
        if (!(Test-Path -LiteralPath $lock -PathType Leaf)) {
            $lock = Join-Path $file.DirectoryName 'packages.lock.json'
        }
        $relativeLock = [IO.Path]::GetRelativePath($repo, $lock).Replace('\', '/')
        $trackedLock = & git --literal-pathspecs ls-files -- $relativeLock
        if ($LASTEXITCODE -ne 0 -or $trackedLock -cne $relativeLock -or
            !(Test-Path -LiteralPath $lock -PathType Leaf) -or !$usedLocks.Add($relativeLock)) {
            throw "A unique tracked NuGet lock file is required for $project"
        }

        # Force a fresh restore of the locked graph and require NuGet's audit counters.
        $restoreArgs = @(
            $file.FullName, '-nologo', '-t:Restore',
            '-getProperty:NuGetLockFilePath,RestoreProjectCount,RestoreProjectsAuditedCount,RestoreSkippedCount',
            '-getItem:NuGetAuditSuppress,PackageReference',
            '-p:Configuration=Release',
            "-p:RestoreConfigFile=$config",
            '-p:RestorePackagesWithLockFile=true',
            '-p:RestoreLockedMode=true',
            '-p:RestoreForce=true',
            '-p:RestoreForceEvaluate=false',
            '-p:RestoreNoHttpCache=true',
            '-p:RestoreIgnoreFailedSources=false',
            '-p:RestoreUseStaticGraphEvaluation=false',
            '-p:NuGetAudit=true',
            '-p:NuGetAuditMode=all',
            '-p:NuGetAuditLevel=low',
            '-p:NoWarn=',
            '-p:WarningsNotAsErrors=',
            '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904%3BNU1905'
        )
        $result = & dotnet msbuild @restoreArgs
        if ($LASTEXITCODE -ne 0) { throw "NuGet audit/restore failed for $project" }
        $audit = ($result -join [Environment]::NewLine) | ConvertFrom-Json
        $properties = $audit.Properties
        $suppressedPackages = @($audit.Items.PackageReference | Where-Object { $_.NoWarn -match '\bNU190[0-5]\b' })
        if ($properties.NuGetLockFilePath -or @($audit.Items.NuGetAuditSuppress).Count -ne 0 -or $suppressedPackages.Count -ne 0 -or
            $properties.RestoreProjectCount -cnotmatch '\A[1-9][0-9]*\z' -or
            $properties.RestoreProjectsAuditedCount -cne $properties.RestoreProjectCount -or
            $properties.RestoreSkippedCount -cne '0') {
            throw "NuGet did not audit every project with the expected lock and unsuppressed advisories for $project"
        }
        & git --literal-pathspecs diff --quiet HEAD -- $relativeLock
        if ($LASTEXITCODE -ne 0) { throw "NuGet changed the tracked lock file for $project" }
        Write-Host "Audited locked NuGet graph for $project"
    }
} finally {
    Pop-Location
}
