using System.Diagnostics;

namespace NvTune;

/// <summary>
/// Portable backgrounds share one global owner and a cooperative stop event across directories.
/// PID files remain local; only known current/legacy executable paths are used for PID migration.
/// </summary>
internal static class DaemonControl
{
    internal const string StopEventName = @"Global\NvTuneDaemonStop";

    private static string PidPath => Path.Combine(Persistence.ActiveDataDir, "daemon.pid");

    internal static void WritePid()
    {
        try
        {
            Directory.CreateDirectory(Persistence.ActiveDataDir);
            File.WriteAllText(PidPath, Environment.ProcessId.ToString());
        }
        catch (Exception ex) { Diag.Log("写入后台任务 PID 失败: " + ex.Message); }
    }

    internal static void ClearPid()
    {
        try
        {
            if (File.Exists(PidPath) && ReadPid() == Environment.ProcessId) File.Delete(PidPath);
        }
        catch (Exception ex) { Diag.Log("清理后台任务 PID 失败: " + ex.Message); }
    }

    private static int? ReadPid()
    {
        try
        {
            return int.TryParse(File.ReadAllText(PidPath).Trim(), out int pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRunning() => GlobalControlLease.IsHeld(GlobalControlLease.DaemonName);

    /// <summary>Starts the current build so recovery uses the same edition and saved settings.</summary>
    public static void Start(bool recoverFans = false)
    {
        if (IsRunning()) return;

        string exe = Product.ExecutablePath();

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = "--daemon --silent" + (recoverFans ? " --recover-fans" : ""),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        Process.Start(psi);
        Diag.Log("已启动后台任务。");
    }

    public static void Stop()
    {
        try
        {
            foreach (var target in StopTargets(Persistence.IsPortable, Product.ExecutablePath(),
                Persistence.LegacyExecutablePath, Persistence.ActiveDataDir, Persistence.LegacyDataDir))
                DaemonStop.Stop(target.PidPath, pid => RecordedProcess.Open(pid, target.ExecutablePaths), Diag.Log);
            // A portable background in another directory has no local PID here. Request its
            // cooperative stop and wait for the global owner before changing configuration.
            DaemonStop.StopGlobal(IsRunning, RequestGlobalStop, Thread.Sleep);
        }
        catch (CliError) { throw; }
        catch (Exception ex) { throw new CliError("停止后台任务失败: " + ex.Message); }
    }

    internal sealed record StopTarget(string PidPath, IReadOnlyList<string> ExecutablePaths);

    /// <summary>Builds exact process-path checks without reading the user's installation. An
    /// installed build may run from Downloads while its existing daemon lives in Program Files;
    /// both known paths must be checked together before a PID can be treated as stale.</summary>
    internal static IReadOnlyList<StopTarget> StopTargets(bool portable, string currentExecutable,
        string installedExecutable, string currentDataDirectory, string installedDataDirectory)
    {
        string currentExe = Path.GetFullPath(currentExecutable);
        string installedExe = Path.GetFullPath(installedExecutable);
        string localPid = Path.Combine(Path.GetFullPath(currentDataDirectory), "daemon.pid");
        string installedPid = Path.Combine(Path.GetFullPath(installedDataDirectory), "daemon.pid");
        bool sharedRecord = string.Equals(localPid, installedPid, StringComparison.OrdinalIgnoreCase);
        string[] localExecutables = (!portable || sharedRecord ? new[] { currentExe, installedExe } : new[] { currentExe })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var local = new StopTarget(localPid, localExecutables);
        return sharedRecord ? new[] { local } : new[] { local, new StopTarget(installedPid, new[] { installedExe }) };
    }

    private static void RequestGlobalStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var stopEvent)) return;
        using (stopEvent) stopEvent.Set();
    }

    private sealed class RecordedProcess(Process process, IReadOnlyList<string> expectedExecutables) : IRecordedDaemon
    {
        public static IRecordedDaemon? Open(int pid, IReadOnlyList<string> expectedExecutables)
        {
            try { return new RecordedProcess(Process.GetProcessById(pid), expectedExecutables); }
            catch (ArgumentException) { return null; }
        }

        public bool IsOurs => process.MainWindowHandle == IntPtr.Zero
            && expectedExecutables.Contains(process.MainModule?.FileName, StringComparer.OrdinalIgnoreCase);
        public bool HasExited => process.HasExited;
        public bool IsVerifiedLegacyDaemon
        {
            get
            {
                if (!IsOurs || process.MainWindowHandle != IntPtr.Zero) return false;
                string path = process.MainModule!.FileName;
                var info = FileVersionInfo.GetVersionInfo(path);
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                if (version >= new Version(1, 0, 2, 0)) return false;
                // Check the live command line, not just its name/PID, before terminating a
                // legacy daemon. WMI is used only for this one-time upgrade path.
                string script = $"$ErrorActionPreference='Stop'; (Get-CimInstance Win32_Process -Filter 'ProcessId={process.Id}').CommandLine";
                var result = ChildProcess.RunPowerShell(script);
                return result.ExitCode == 0 && DaemonStop.IsLegacy(version, false, result.Output);
            }
        }

        public bool TryRequestStop()
        {
            if (!EventWaitHandle.TryOpenExisting(StopEventName, out var stopEvent)) return false;
            using (stopEvent) stopEvent.Set();
            return true;
        }

        public bool WaitForExit(int timeoutMs) => process.WaitForExit(timeoutMs);
        public void TerminateLegacy() => process.Kill();
        public void Dispose() => process.Dispose();
    }
}
