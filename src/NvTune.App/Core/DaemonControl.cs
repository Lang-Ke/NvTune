using System.Diagnostics;

namespace NvTune;

/// <summary>
/// Starts, stops and detects the background task. The daemon records its PID in the install data
/// folder so the GUI can stop exactly that process (never a name-based sweep), and a named mutex
/// guarantees a single instance even if both the logon task and the GUI try to start one.
/// </summary>
internal static class DaemonControl
{
    internal const string StopEventName = @"Global\NvTuneDaemonStop";

    private static string PidPath => Path.Combine(Persistence.DataDir(), "daemon.pid");

    internal static void WritePid()
    {
        try { File.WriteAllText(PidPath, Environment.ProcessId.ToString()); }
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

    /// <summary>True when the recorded PID is alive and really is this app.</summary>
    public static bool IsRunning()
    {
        if (ReadPid() is not { } pid) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited && IsOurs(proc);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOurs(Process proc)
        => string.Equals(proc.MainModule?.FileName, Persistence.InstallExePath(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(proc.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the installed copy with <c>--daemon</c>. Prefers the installed path: the daemon must
    /// keep running after the window closes, and starting it from a folder the user may delete would
    /// make it fail on the next logon.
    /// </summary>
    public static void Start(bool recoverFans = false)
    {
        if (IsRunning()) return;

        // A GUI standby must run the exact same build, even before the user saves/updates
        // an older installed copy. An older daemon does not understand the ownership lease.
        string exe = recoverFans
            ? Environment.ProcessPath ?? throw new CliError("无法确定程序路径。")
            : File.Exists(Persistence.InstallExePath())
            ? Persistence.InstallExePath()
            : Environment.ProcessPath ?? throw new CliError("无法确定程序路径。");

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
        try { DaemonStop.Stop(PidPath, RecordedProcess.Open, Diag.Log); }
        catch (CliError) { throw; }
        catch (Exception ex) { throw new CliError("停止后台任务失败: " + ex.Message); }
    }

    private sealed class RecordedProcess(Process process) : IRecordedDaemon
    {
        public static IRecordedDaemon? Open(int pid)
        {
            try { return new RecordedProcess(Process.GetProcessById(pid)); }
            catch (ArgumentException) { return null; }
        }

        public bool IsOurs => process.MainWindowHandle == IntPtr.Zero && DaemonControl.IsOurs(process);
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
