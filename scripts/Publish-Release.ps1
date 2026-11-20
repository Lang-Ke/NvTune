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
$portableArchiveName = 'NvTunePortable-win-x64.zip'
$expectedNames = @('NvTuneApp.exe', $portableArchiveName)

function Assert-ReleaseContents {
    if (!(Test-Path -LiteralPath $destination)) { return }
    $unexpected = @(Get-ChildItem -LiteralPath $destination -Force | Where-Object {
        $_.PSIsContainer -or $_.Name -cnotin $expectedNames
    })
    if ($unexpected.Count -ne 0) {
        throw "Release directory contains unexpected entries: $($unexpected.Name -join ', '). Use an empty output directory."
    }
}

function Assert-DirectoryContents([string]$Directory, [string[]]$FileNames) {
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    $unexpected = @($entries | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $FileNames })
    if ($entries.Count -ne $FileNames.Count -or $unexpected.Count -ne 0) {
        throw "Expected exactly $($FileNames -join ', '), without extra files or directories, in $Directory."
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
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
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

function Test-Package([string]$Executable, [string]$Edition, [string]$ExpectedDataDirectory,
    [string]$WorkingDirectory, [string[]]$ExpectedSiblingFiles) {
    # Preview rendering avoids GPU, installation, settings, and scheduled tasks.
    # Check both directories before and after running the final executable name.
    $appDirectory = Split-Path -Parent $Executable
    Assert-DirectoryContents -Directory $appDirectory -FileNames $ExpectedSiblingFiles
    Assert-DirectoryContents -Directory $WorkingDirectory -FileNames @()
    $report = Invoke-AppCheck -Executable $Executable -Argument '--verify-package' -WorkingDirectory $WorkingDirectory
    $lines = $report -split '\r?\n'
    foreach ($expectedLine in @('Deployment: single-file', "Edition: $Edition",
        "Data directory: $ExpectedDataDirectory", 'Package check passed. No GPU APIs called.')) {
        if ($lines -cnotcontains $expectedLine) {
            throw "Package verification is missing '$expectedLine'. $report"
        }
    }
    Assert-DirectoryContents -Directory $appDirectory -FileNames $ExpectedSiblingFiles
    Assert-DirectoryContents -Directory $WorkingDirectory -FileNames @()
    Write-Output $report
}

function Assert-MatchingHash([string]$Original, [string]$Extracted) {
    $originalHash = (Get-FileHash -LiteralPath $Original -Algorithm SHA256).Hash
    $extractedHash = (Get-FileHash -LiteralPath $Extracted -Algorithm SHA256).Hash
    if ($originalHash -cne $extractedHash) {
        throw "Extracted file does not match its source: $Extracted."
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
        Assert-DirectoryContents -Directory $appDirectory -FileNames @('NvTuneApp.exe')
        if ($edition.FileName -cne 'NvTuneApp.exe') {
            Rename-Item -LiteralPath (Join-Path $appDirectory 'NvTuneApp.exe') -NewName $edition.FileName
        }

        $executable = Join-Path $appDirectory $edition.FileName
        $expectedDataDirectory = if ($edition.Portable -ceq 'true') {
            Join-Path $appDirectory 'data'
        } else {
            Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'NvTune/data'
        }
        Test-Package -Executable $executable -Edition $edition.Name -ExpectedDataDirectory $expectedDataDirectory `
            -WorkingDirectory $checkDirectory -ExpectedSiblingFiles @($edition.FileName)
        $edition.Executable = $executable
    }

    $portable = $editions | Where-Object { $_.Name -ceq 'portable' }
    $instructionsPath = Join-Path $staging '使用说明.txt'
    $instructions = @'
NvTune 便携版

1. 先完整解压 NvTunePortable 文件夹，再运行其中的 NvTunePortable.exe；请勿直接在 ZIP 中运行。
2. 首次运行会自动生成 data 文件夹，保存配置和日志；移动时请携带整个文件夹，升级时保留原 data。
3. 更新或移动前，先关闭 NvTune 窗口，再在当前程序目录的管理员 PowerShell 执行：
   $stop = Start-Process ".\NvTunePortable.exe" -ArgumentList "--stop-background" -Wait -PassThru
   if ($stop.ExitCode -ne 0) { throw "后台停止失败，请勿更新或移动。" }
   仅在命令成功后替换程序或移动文件夹；停止后台不会删除已保存设置和登录恢复任务。
4. 更新程序或移动目录后，请运行软件并再次点击“保存并开机应用”，刷新登录任务的路径和程序校验值。
5. 运行环境：64 位 Windows、NVIDIA 显卡及驱动、.NET 10 Desktop Runtime x64；运行需要管理员权限。
'@
    [IO.File]::WriteAllText($instructionsPath, $instructions, [Text.UTF8Encoding]::new($false))
    $archivePath = Join-Path $staging $portableArchiveName
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $portable.Executable,
            'NvTunePortable/NvTunePortable.exe', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $instructionsPath,
            'NvTunePortable/使用说明.txt', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    } finally {
        $archive.Dispose()
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expectedEntries = @('NvTunePortable/NvTunePortable.exe', 'NvTunePortable/使用说明.txt')
        if ($archive.Entries.Count -ne 2 -or
            @($archive.Entries | Where-Object { $_.FullName -cnotin $expectedEntries }).Count -ne 0 -or
            @($archive.Entries.FullName | Sort-Object -Unique).Count -ne 2) {
            throw 'Portable archive must contain exactly the executable and usage instructions under NvTunePortable/.'
        }
    } finally {
        $archive.Dispose()
    }

    # Exercise the actual distributed ZIP after extracting to a different path
    # containing both spaces and Chinese characters. No data is bundled.
    $extractionDirectory = Join-Path $staging '解压 验证目录'
    $extractedAppDirectory = Join-Path $extractionDirectory 'NvTunePortable'
    $extractedWorkingDirectory = Join-Path $staging '空白 工作目录'
    New-Item -ItemType Directory -Path $extractedWorkingDirectory -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractionDirectory)
    $extractionEntries = @(Get-ChildItem -LiteralPath $extractionDirectory -Force)
    if ($extractionEntries.Count -ne 1 -or !$extractionEntries[0].PSIsContainer -or
        $extractionEntries[0].Name -cne 'NvTunePortable') {
        throw 'Portable extraction must contain only the NvTunePortable directory.'
    }
    $extractedExe = Join-Path $extractedAppDirectory 'NvTunePortable.exe'
    $extractedInstructions = Join-Path $extractedAppDirectory '使用说明.txt'
    Assert-MatchingHash -Original $portable.Executable -Extracted $extractedExe
    Assert-MatchingHash -Original $instructionsPath -Extracted $extractedInstructions
    Test-Package -Executable $extractedExe -Edition 'portable' `
        -ExpectedDataDirectory (Join-Path $extractedAppDirectory 'data') `
        -WorkingDirectory $extractedWorkingDirectory -ExpectedSiblingFiles @('NvTunePortable.exe', '使用说明.txt')
    Assert-MatchingHash -Original $portable.Executable -Extracted $extractedExe
    Assert-MatchingHash -Original $instructionsPath -Extracted $extractedInstructions

    # Only update the release directory once both editions and the ZIP pass.
    Assert-ReleaseContents
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $releaseFiles = @(
        ($editions | Where-Object { $_.Name -ceq 'installed' }).Executable,
        $archivePath
    )
    foreach ($sourceFile in $releaseFiles) {
        $fileName = Split-Path -Leaf $sourceFile
        $releasePath = Join-Path $destination $fileName
        Copy-Item -LiteralPath $sourceFile -Destination $releasePath -Force
        $digest = (Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Get-Item -LiteralPath $releasePath | Select-Object Name, Length, FullName
        Write-Output "$fileName SHA256: $digest"
    }
    Assert-ReleaseContents
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 2) {
        throw 'Expected exactly NvTuneApp.exe and NvTunePortable-win-x64.zip in the release directory.'
    }
} finally {
    Pop-Location
}
