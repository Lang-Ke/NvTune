using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

class Launcher
{
    [STAThread]
    static int Main()
    {
        bool hasDesktop = false;
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                foreach (string line in output.Split('\n'))
                {
                    if (line.Contains("Microsoft.WindowsDesktop.App") && line.Contains(" 10."))
                    {
                        hasDesktop = true;
                        break;
                    }
                }
            }
        }
        catch { }

        if (!hasDesktop && File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"dotnet\dotnet.exe")))
        {
            hasDesktop = HasDesktopRuntime(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"dotnet\dotnet.exe"));
        }

        if (!hasDesktop && File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\dotnet\dotnet.exe")))
        {
            hasDesktop = HasDesktopRuntime(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\dotnet\dotnet.exe"));
        }

        if (hasDesktop)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NvTuneApp.exe");
            if (File.Exists(exePath))
            {
                Process.Start(exePath);
                return 0;
            }
            return 1;
        }

        string msg = "未检测到 .NET 10 运行时。\n\n请访问以下网址下载并安装 .NET Desktop Runtime 10.0:\nhttps://dotnet.microsoft.com/zh-cn/download\n\n安装完成后重新运行本程序。";
        System.Windows.Forms.MessageBox.Show(msg, "缺少 .NET 运行时", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        Process.Start("https://dotnet.microsoft.com/zh-cn/download");
        return 2;
    }

    static bool HasDesktopRuntime(string dotnetPath)
    {
        try
        {
            var psi = new ProcessStartInfo(dotnetPath, "--list-runtimes")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Split('\n').Any(line =>
                line.Contains("Microsoft.WindowsDesktop.App") && line.Contains(" 10."));
        }
        catch
        {
            return false;
        }
    }
}
