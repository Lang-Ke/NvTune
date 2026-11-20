using System.Text.RegularExpressions;

namespace NvTune;

internal interface IRecordedDaemon : IDisposable
{
    bool IsOurs { get; }
    bool HasExited { get; }
    bool IsVerifiedLegacyDaemon { get; }
    bool TryRequestStop();
    bool WaitForExit(int timeoutMs);
    void TerminateLegacy();
}

internal static class DaemonStop
{
    internal static bool IsLegacy(Version version, bool hasWindow, string commandLine)
        => version >= new Version(1, 0, 0, 0) && version < new Version(1, 0, 2, 0) && !hasWindow
            && Regex.IsMatch(commandLine, @"(?:^|\s)--daemon(?:\s|$)", RegexOptions.CultureInvariant);

    internal static void Stop(string pidPath, Func<int, IRecordedDaemon?> open, Action<string> log)
    {
        int? pid = ReadPid(pidPath);
        if (pid is null) return;
        try
        {
            using var process = open(pid.Value);
            if (process is null || process.HasExited || !process.IsOurs) return;
            if (process.IsVerifiedLegacyDaemon)
            {
                // Only a verified pre-protocol daemon may be terminated for migration.
                // Never turn a modern daemon's timeout into a forced termination.
                process.TerminateLegacy();
                if (!process.WaitForExit(5000)) throw new CliError("旧版后台退出失败，请先退出旧版后重试。");
                log($"已退出不支持停止请求的旧版后台 (PID {pid})，原配置文件保留。");
            }
            else if (process.TryRequestStop())
            {
                if (!process.WaitForExit(15000))
                    throw new CliError("后台未能安全停止，请稍后重试。当前操作已取消。");
            }
            else throw new CliError("无法确认后台的停止接口，请先退出其他 NvTune 后重试。");
            log($"已停止后台任务 (PID {pid})。");
        }
        finally
        {
            // A failed/timeout stop must leave a live daemon's PID intact. A stale PID must
            // not prevent initialization. Never delete a replacement daemon's newer record.
            if (ReadPid(pidPath) == pid)
            {
                using var remaining = open(pid.Value);
                if (remaining is null || remaining.HasExited || !remaining.IsOurs) File.Delete(pidPath);
            }
        }
    }

    internal static int? ReadPid(string path)
    {
        try { return int.TryParse(File.ReadAllText(path).Trim(), out int pid) && pid > 0 ? pid : null; }
        catch (IOException) { return null; }
    }
}
