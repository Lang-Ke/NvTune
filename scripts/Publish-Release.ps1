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
# Each run builds into a fresh directory, so a successful check cannot depend on
# stale DLLs, runtime configuration, or a separate launcher.
$staging = Join-Path $repoRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $staging 'app'
$checkDirectory = Join-Path $staging 'check-working-directory'
New-Item -ItemType Directory -Path $checkDirectory -Force | Out-Null

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

function Invoke-AppCheck([string]$Executable, [string]$Argument) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.Arguments = $Argument
    $start.WorkingDirectory = $checkDirectory
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
    Invoke-DotNet -Arguments @('publish', 'src/NvTune.App/NvTune.csproj', '-c', 'Release',
        '-p:PublishProfile=WinX64', '-o', $appDirectory)
    $published = @(Get-ChildItem -LiteralPath $appDirectory -File)
    if ($published.Count -ne 1 -or $published[0].Name -ne 'NvTuneApp.exe' -or
        @(Get-ChildItem -LiteralPath $appDirectory -Directory).Count -ne 0) {
        throw 'Expected exactly one published NvTuneApp.exe, without sidecar files.'
    }

    # These modes never load GPU APIs, install files, change saved configurations,
    # or register a Windows task. Run from an empty working directory as well as
    # a fresh publish directory to catch dependencies on neighboring files.
    $executable = Join-Path $appDirectory 'NvTuneApp.exe'
    $report = Invoke-AppCheck -Executable $executable -Argument '--verify-package'
    if (!$report.Contains('Deployment: single-file') -or
        !$report.Contains('Package check passed. No GPU APIs called.')) {
        throw "Single-file verification failed. $report"
    }
    Write-Output $report
    $license = Invoke-AppCheck -Executable $executable -Argument '--license'
    $expectedLicense = [IO.File]::ReadAllText((Join-Path $repoRoot 'LICENSE'))
    if ($license.Replace("`r`n", "`n").Trim() -cne $expectedLicense.Replace("`r`n", "`n").Trim()) {
        throw 'Embedded license does not match the complete repository LICENSE.'
    }
    Write-Output 'Embedded license check passed.'

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $executablePath = Join-Path $destination 'NvTuneApp.exe'
    Copy-Item -LiteralPath $executable -Destination $executablePath -Force
    $digest = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Get-Item -LiteralPath $executablePath | Select-Object Name, Length, FullName
    Write-Output "SHA256: $digest"
} finally {
    Pop-Location
}
