param(
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
# Each edition gets fresh build, publish, and working directories, so checks
# cannot depend on artifacts from another edition or a previous release.
$staging = Join-Path $repoRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$editions = @(
    @{ Name = 'installed'; Portable = 'false'; FileName = 'NvTuneApp.exe' },
    @{ Name = 'portable'; Portable = 'true'; FileName = 'NvTunePortable.exe' }
)
$expectedNames = @($editions | ForEach-Object { $_.FileName })

function Assert-ReleaseContents {
    if (!(Test-Path -LiteralPath $destination)) { return }
    $unexpected = @(Get-ChildItem -LiteralPath $destination -Force | Where-Object {
        $_.PSIsContainer -or $_.Name -cnotin $expectedNames
    })
    if ($unexpected.Count -ne 0) {
        throw "Release directory contains unexpected entries: $($unexpected.Name -join ', '). Use an empty output directory."
    }
}

function Assert-SingleExecutable([string]$Directory, [string]$FileName) {
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($entries.Count -ne 1 -or $entries[0].PSIsContainer -or $entries[0].Name -cne $FileName) {
        throw "Expected exactly one published $FileName, without sidecar files or data directories, in $Directory."
    }
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

function Invoke-AppCheck([string]$Executable, [string]$Argument, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.Arguments = $Argument
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill()
            throw "Application check $Argument timed out."
        }
        $report = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Application check $Argument failed (exit $($process.ExitCode)). $report $errors"
        }
        return $report.Trim()
    } finally {
        $process.Dispose()
    }
}

Push-Location $repoRoot
try {
    Assert-ReleaseContents
    foreach ($edition in $editions) {
        $editionDirectory = Join-Path $staging $edition.Name
        $appDirectory = Join-Path $editionDirectory 'app'
        $checkDirectory = Join-Path $editionDirectory 'check-working-directory'
        $buildDirectory = Join-Path $editionDirectory 'build'
        New-Item -ItemType Directory -Path $checkDirectory -Force | Out-Null
        Invoke-DotNet -Arguments @('publish', 'src/NvTune.App/NvTune.csproj', '-c', 'Release',
            '-p:PublishProfile=WinX64', "-p:Portable=$($edition.Portable)",
            '--artifacts-path', $buildDirectory, '-o', $appDirectory)
        Assert-SingleExecutable -Directory $appDirectory -FileName 'NvTuneApp.exe'
        if ($edition.FileName -cne 'NvTuneApp.exe') {
            Rename-Item -LiteralPath (Join-Path $appDirectory 'NvTuneApp.exe') -NewName $edition.FileName
        }

        # Package verification renders the UI without GPU, installation, data
        # initialization, or task registration. Verify the final executable name.
        $executable = Join-Path $appDirectory $edition.FileName
        $report = Invoke-AppCheck -Executable $executable -Argument '--verify-package' -WorkingDirectory $checkDirectory
        $expectedDataDirectory = if ($edition.Portable -ceq 'true') {
            Join-Path $appDirectory 'data'
        } else {
            Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'NvTune/data'
        }
        $lines = $report -split '\r?\n'
        foreach ($expectedLine in @('Deployment: single-file', "Edition: $($edition.Name)",
            "Data directory: $expectedDataDirectory", 'Package check passed. No GPU APIs called.')) {
            if ($lines -cnotcontains $expectedLine) {
                throw "Package verification is missing '$expectedLine'. $report"
            }
        }
        Assert-SingleExecutable -Directory $appDirectory -FileName $edition.FileName
        if (@(Get-ChildItem -LiteralPath $checkDirectory -Force).Count -ne 0) {
            throw "Package verification created files in its working directory: $checkDirectory."
        }
        Write-Output $report
        $edition.Executable = $executable
    }

    # Only update the release directory once both editions have passed.
    Assert-ReleaseContents
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($edition in $editions) {
        $executablePath = Join-Path $destination $edition.FileName
        Copy-Item -LiteralPath $edition.Executable -Destination $executablePath -Force
        $digest = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Get-Item -LiteralPath $executablePath | Select-Object Name, Length, FullName
        Write-Output "$($edition.FileName) SHA256: $digest"
    }
    Assert-ReleaseContents
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 2) {
        throw 'Expected exactly NvTuneApp.exe and NvTunePortable.exe in the release directory.'
    }
} finally {
    Pop-Location
}
