using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Starts, stops and detects the background task. The daemon records its PID in the install data
/// folder so the GUI can stop exactly that process (never a name-based sweep), and a named mutex
/// guarantees a single instance even if both the logon task and the GUI try to start one.
/// </summary>
internal static class DaemonControl
{
    internal const string MutexName = @"Local\NvTuneDaemon";

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
        => string.Equals(proc.ProcessName, AppName, StringComparison.OrdinalIgnoreCase);

    private static string AppName => Path.GetFileNameWithoutExtension(Persistence.InstallExePath());

    /// <summary>
    /// Starts the installed copy with <c>--daemon</c>. Prefers the installed path: the daemon must
    /// keep running after the window closes, and starting it from a folder the user may delete would
    /// make it fail on the next logon.
    /// </summary>
    public static void Start()
    {
        if (IsRunning()) return;

        string exe = File.Exists(Persistence.InstallExePath())
            ? Persistence.InstallExePath()
            : Environment.ProcessPath ?? throw new CliError("无法确定程序路径。");

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = "--daemon --silent",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Persistence.InstallDir(),
        };
        Process.Start(psi);
        Diag.Log("已启动后台任务。");
    }

    public static void Stop()
    {
        if (ReadPid() is { } pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited && IsOurs(proc))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                    Diag.Log($"已停止后台任务 (PID {pid})。");
                }
            }
            catch (Exception ex)
            {
                Diag.Log("停止后台任务失败: " + ex.Message);
            }
        }

        ClearPid();
    }
}
