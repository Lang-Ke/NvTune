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
# Each run builds into a fresh directory, so stale DLLs/PDBs cannot enter the ZIP.
$staging = Join-Path $repoRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $staging 'app'
$launcherDirectory = Join-Path $staging 'launcher'
$packageDirectory = Join-Path $staging 'package'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
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
    Invoke-DotNet -Arguments @('build', 'src/NvTune.Launcher/Launcher.csproj', '-c', 'Release',
        '-o', $launcherDirectory)
    Copy-Item -LiteralPath (Join-Path $appDirectory 'NvTuneApp.exe') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $launcherDirectory '启动器.exe') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $packageDirectory

    # Only this mode creates an offscreen sample UI. It never loads GPU APIs, installs
    # the app, changes saved configurations, or registers a Windows task.
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $packageDirectory 'NvTuneApp.exe'))
    $start.Arguments = '--verify-package'
    $start.WorkingDirectory = $packageDirectory
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
            throw 'Package verification timed out.'
        }
        $report = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or !$report.Contains('Deployment: single-file') -or
            !$report.Contains('Package check passed. No GPU APIs called.')) {
            throw "Package verification failed (exit $($process.ExitCode)). $report $errors"
        }
        Write-Output $report.Trim()
    } finally {
        $process.Dispose()
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $stagedZip = Join-Path $staging 'NvTune-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $stagedZip,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($stagedZip)
    try {
        $expected = @('LICENSE', 'NvTuneApp.exe', '启动器.exe')
        $actual = @($zip.Entries.FullName | Sort-Object)
        if (@(Compare-Object ($expected | Sort-Object) $actual).Count -ne 0) {
            throw 'ZIP content does not match the three-file release manifest.'
        }
    } finally {
        $zip.Dispose()
    }
    $zipPath = Join-Path $destination 'NvTune-win-x64.zip'
    Copy-Item -LiteralPath $stagedZip -Destination $zipPath -Force
    $digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'),
        "$digest  NvTune-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $zipPath | Select-Object Name, Length, FullName
    Write-Output "SHA256: $digest"
} finally {
    Pop-Location
}
