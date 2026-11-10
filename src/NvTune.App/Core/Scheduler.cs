using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// One logon task, registered from XML, that runs the installed copy with <c>--daemon</c>: it
/// re-applies every saved undervolt and fan setting after a reboot. A single task keeps the two
/// settings in a defined order (undervolt first, then fans) instead of racing two processes, and
/// because it targets the copy in Program Files, deleting or moving the unzipped folder no longer
/// breaks it. XML is used rather than <c>/TR</c> so the quoted path, the arguments and an unlimited
/// run time for a fan curve are all expressible.
/// </summary>
internal static class Scheduler
{
    public const string TaskName = "NvTuneDaemon";

    /// <summary>Registers (or refreshes) the task. Throws when Task Scheduler refuses, and verifies
    /// the registration afterwards: the caller must never claim "survives reboot" on a task that
    /// isn't there.</summary>
    public static void Register()
    {
        string exe = Persistence.InstallExePath();
        if (!File.Exists(exe))
        {
            throw new CliError($"安装目录中找不到 {exe}，无法注册开机任务。请先点击“应用”完成安装。");
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"nvtune-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildXml(exe, "--daemon --silent"),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var (code, _, stderr) = Run("schtasks.exe", "/Create", "/F", "/TN", TaskName, "/XML", xmlPath);
            if (code != 0)
            {
                throw new CliError($"注册开机任务失败: {Clean(stderr)}");
            }
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }

        if (!Exists())
        {
            throw new CliError("注册开机任务后未能查询到该任务，Windows 任务计划程序可能拒绝了写入。");
        }

        Diag.Log("已注册登录任务 " + TaskName);
    }

    public static bool Exists()
    {
        var (code, _, _) = Run("schtasks.exe", "/Query", "/TN", TaskName);
        return code == 0;
    }

    /// <summary>The task's command line, for the status panel; null when it can't be read.</summary>
    public static string? Describe()
    {
        var (code, stdout, _) = Run("schtasks.exe", "/Query", "/TN", TaskName, "/FO", "LIST", "/V");
        if (code != 0) return null;
        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("要运行的任务", StringComparison.Ordinal)
                || trimmed.StartsWith("Task To Run", StringComparison.OrdinalIgnoreCase))
            {
                int colon = trimmed.IndexOf(':');
                if (colon >= 0) return trimmed[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    public static void RemoveLegacyTasks()
    {
        foreach (string name in new[] { "NvTuneUndervolt", "NvTuneFanDaemon" })
        {
            var (legacyCode, _, _) = Run("schtasks.exe", "/Delete", "/TN", name, "/F");
            if (legacyCode == 0) Diag.Log("已删除旧登录任务 " + name);
        }
    }

    public static void Remove()
    {
        if (!Exists()) return;
        var (code, _, stderr) = Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        if (code != 0)
        {
            throw new CliError($"删除开机任务失败: {Clean(stderr)}");
        }

        Diag.Log("已删除登录任务 " + TaskName);
    }

    private static string BuildXml(string exe, string arguments)
    {
        string exeXml = SecurityElement.Escape(exe)!;
        string argsXml = SecurityElement.Escape(arguments)!;
        string userId = SecurityElement.Escape(CurrentUserSid())!;
        string workDir = SecurityElement.Escape(Persistence.InstallDir())!;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>NvTune: 登录后重新应用显卡降压与风扇设置</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT20S</Delay>
      <UserId>{userId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>6</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exeXml}</Command>
      <Arguments>{argsXml}</Arguments>
      <WorkingDirectory>{workDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } sid) return sid.Value;
        }
        catch (Exception ex)
        {
            Diag.Log("读取当前用户 SID 失败: " + ex.Message);
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    private static string Clean(string text) => text.Trim().Replace("\r", " ").Replace("\n", " ");

    private static (int Code, string Stdout, string Stderr) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
