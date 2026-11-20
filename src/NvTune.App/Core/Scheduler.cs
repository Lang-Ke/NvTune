using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace NvTune;

/// <summary>
/// One logon task, registered from XML, that runs the selected edition with <c>--daemon</c>: it
/// re-applies every saved undervolt and fan setting after a reboot. A single task keeps the two
/// settings in a defined order (undervolt first, then fans) instead of racing two processes.
/// Installed builds target Program Files; saving a portable build refreshes its path and digest.
/// XML is used rather than
/// <c>/TR</c> so paths, arguments and an unlimited run time for a fan curve are all expressible.
/// </summary>
internal static class Scheduler
{
    public const string TaskName = "NvTuneDaemon";
    private const string DaemonArguments = "--daemon --silent";
    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory,
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Registers (or refreshes) the task. Throws when Task Scheduler refuses, and verifies
    /// the registration afterwards: the caller must never claim "survives reboot" on a task that
    /// isn't there.</summary>
    public static void Register()
    {
        string exe = Persistence.IsPortable ? Product.ExecutablePath() : Persistence.InstallExePath();
        if (!File.Exists(exe))
        {
            throw new CliError($"找不到登录任务所需程序 {exe}，请重新保存设置后重试。");
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"nvtune-{Guid.NewGuid():N}.xml");
        try
        {
            string xml = Persistence.IsPortable
                ? BuildXml(exe, DaemonArguments, CurrentUserSid(), FileHash(exe))
                : BuildInstalledXml(exe, DaemonArguments, CurrentUserSid());
            File.WriteAllText(xmlPath, xml,
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
        try
        {
            string? xml = ReadTaskXml();
            if (xml is null) return false;
            string exe = Persistence.IsPortable ? Product.ExecutablePath() : Persistence.InstallExePath();
            return Persistence.IsPortable
                ? MatchesCurrentExecutable(xml, exe, FileHash(exe))
                : MatchesInstalledExecutable(xml, exe);
        }
        catch (Exception ex)
        {
            Diag.Log("核对登录任务失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>The registered target path, including an older portable location.</summary>
    public static string? Describe()
    {
        try
        {
            string? xml = ReadTaskXml();
            if (xml is null) return null;
            var task = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return (string?)task.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Source")
                ?? (string?)task.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command");
        }
        catch { return null; }
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
        var (queryCode, _, _) = Run("schtasks.exe", "/Query", "/TN", TaskName);
        if (queryCode != 0) return;
        var (code, _, stderr) = Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        if (code != 0)
        {
            throw new CliError($"删除开机任务失败: {Clean(stderr)}");
        }

        Diag.Log("已删除登录任务 " + TaskName);
    }

    internal static string BuildXml(string exe, string arguments, string userId, string sha256)
        => BuildTaskXml(exe, PowerShellPath, BuildLauncherArguments(exe, arguments, sha256), userId);

    internal static string BuildInstalledXml(string exe, string arguments, string userId)
        => BuildTaskXml(exe, Path.GetFullPath(exe), arguments, userId);

    private static string BuildTaskXml(string exe, string launcher, string arguments, string userId)
    {
        exe = Path.GetFullPath(exe);
        string exeXml = SecurityElement.Escape(exe)!;
        string launcherXml = SecurityElement.Escape(launcher)!;
        string argsXml = SecurityElement.Escape(arguments)!;
        string userXml = SecurityElement.Escape(userId)!;
        string workDir = SecurityElement.Escape(Path.GetDirectoryName(Path.GetFullPath(exe))!)!;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>NvTune: 登录后重新应用显卡降压与风扇设置</Description>
    <Source>{exeXml}</Source>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT20S</Delay>
      <UserId>{userXml}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userXml}</UserId>
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
      <Command>{launcherXml}</Command>
      <Arguments>{argsXml}</Arguments>
      <WorkingDirectory>{workDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    internal static bool MatchesInstalledExecutable(string xml, string exe)
    {
        try
        {
            exe = Path.GetFullPath(exe);
            var task = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = task.Root?.Element(ns + "Actions");
            if (actions is null || actions.Elements().Count() != 1) return false;
            var action = actions.Element(ns + "Exec");
            return action is not null
                && string.Equals((string?)action.Element(ns + "Command"), exe, StringComparison.OrdinalIgnoreCase)
                && (string?)action.Element(ns + "Arguments") == DaemonArguments
                && string.Equals((string?)action.Element(ns + "WorkingDirectory"), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Only a task bound to this exact executable and digest counts as enabled.</summary>
    internal static bool MatchesCurrentExecutable(string xml, string exe, string sha256)
    {
        try
        {
            exe = Path.GetFullPath(exe);
            var task = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = task.Root?.Element(ns + "Actions");
            if (actions is null || actions.Elements().Count() != 1) return false;
            var action = actions.Element(ns + "Exec");
            return action is not null
                && string.Equals((string?)action.Element(ns + "Command"), PowerShellPath, StringComparison.OrdinalIgnoreCase)
                && (string?)action.Element(ns + "Arguments") == BuildLauncherArguments(exe, DaemonArguments, sha256)
                && string.Equals((string?)action.Element(ns + "WorkingDirectory"), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase)
                && string.Equals((string?)task.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Source"), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static string BuildLauncherArguments(string exe, string arguments, string sha256)
    {
        string script = BuildLauncherScript(exe, arguments, sha256);
        return "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>The protected task stores the script and expected hash; portable data is never executed.</summary>
    internal static string BuildLauncherScript(string exe, string arguments, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A complete SHA-256 digest is required.", nameof(sha256));
        exe = Path.GetFullPath(exe);
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
$ErrorActionPreference = 'Stop'
$exe = {{Literal(exe)}}
$arguments = {{Literal(arguments)}}
$workingDirectory = {{Literal(Path.GetDirectoryName(exe)!)}}
$expected = '{{sha256.ToUpperInvariant()}}'
$stream = $null
$hasher = $null
try {
    $stream = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hasher = [Security.Cryptography.SHA256]::Create()
    $actual = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
    if (-not [String]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'NvTune executable changed. Save the settings again to update the logon task.'
    }
    $started = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru
    if ($null -eq $started) { throw 'NvTune background process did not start.' }
    $started.Dispose()
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($null -ne $hasher) { $hasher.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}
exit 0
""";
    }

    private static string FileHash(string exe)
    {
        using var stream = File.OpenRead(exe);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? ReadTaskXml()
    {
        var (code, stdout, _) = Run("schtasks.exe", "/Query", "/TN", TaskName, "/XML");
        return code == 0 ? stdout : null;
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
        var result = ChildProcess.Run(exe, args);
        return (result.ExitCode, result.Output, result.Error);
    }
}
