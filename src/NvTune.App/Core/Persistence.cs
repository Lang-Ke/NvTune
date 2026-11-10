using System.Diagnostics;
using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Where the app keeps itself and its state. The GUI may be started from anywhere (a zip folder,
/// the desktop, a USB stick), but everything that has to survive a reboot lives in one stable
/// place: %ProgramFiles%\NvTune. The logon task always points there, so moving or deleting the
/// folder the user unzipped can no longer break the re-apply. Program Files is admin-only writable,
/// which is also what lets the elevated logon task trust the files it runs.
/// </summary>
internal static class Persistence
{
    public const string FolderName = "NvTune";

    /// <summary>The folder this process was started from (the unzipped folder during a first run).</summary>
    public static string SourceDir() => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string InstallDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName);

    public static string InstallExePath() => Path.Combine(InstallDir(), "NvTuneApp.exe");

    public static bool RunningInstalledCopy()
        => string.Equals(Normalize(Environment.ProcessPath), Normalize(InstallExePath()),
            StringComparison.OrdinalIgnoreCase);

    public static string DataDir()
    {
        string dir = Path.Combine(InstallDir(), "data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DataFilePath(string stem, int gpuIndex)
        => Path.Combine(DataDir(), gpuIndex == 0 ? $"{stem}.json" : $"{stem}.gpu{gpuIndex}.json");

    public static IReadOnlyList<int> StoredGpuIndexes(string stem)
    {
        string dir = Path.Combine(InstallDir(), "data");
        if (!Directory.Exists(dir))
        {
            return Array.Empty<int>();
        }

        try
        {
            return Directory.GetFiles(dir, $"{stem}*.json")
                .Select(file => DataFileGpuIndex(stem, Path.GetFileName(file)))
                .OfType<int>()
                .Order()
                .ToArray();
        }
        catch (Exception ex)
        {
            Diag.Log("枚举数据文件失败: " + ex.Message);
            return Array.Empty<int>();
        }
    }

    public static int? DataFileGpuIndex(string stem, string fileName)
    {
        if (fileName.Equals($"{stem}.json", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string prefix = $"{stem}.gpu";
        const string suffix = ".json";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string middle = fileName[prefix.Length..^suffix.Length];
        return int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index
            : null;
    }

    /// <summary>
    /// Copies the running program into the stable install folder. Returns the folder it installed
    /// into, or null when this process already is that copy. Cheap enough to call before every
    /// save: an already-installed run does no I/O beyond the path comparison.
    /// </summary>
    public static string? EnsureInstalled()
    {
        if (RunningInstalledCopy())
        {
            return null;
        }

        string source = SourceDir();
        string target = InstallDir();
        Directory.CreateDirectory(target);
        StopOtherAppProcesses();

        foreach (string file in Directory.GetFiles(source, "NvTuneApp.*"))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string copy = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, copy, overwrite: true);
            RemoveMarkOfTheWeb(copy);
        }

        // Carry a config saved by an earlier portable build over, so updating does not lose it.
        string oldData = Path.Combine(source, "data");
        if (Directory.Exists(oldData))
        {
            string newData = Path.Combine(target, "data");
            Directory.CreateDirectory(newData);
            foreach (string file in Directory.GetFiles(oldData, "*.json"))
            {
                string copy = Path.Combine(newData, Path.GetFileName(file));
                if (!File.Exists(copy))
                {
                    try { File.Copy(file, copy); } catch (Exception ex) { Diag.Log("迁移配置失败: " + ex.Message); }
                }
            }
        }

        Diag.Log($"已安装到 {target}");
        return target;
    }

    /// <summary>Removes the download mark from a copied file: File.Copy carries alternate data
    /// streams along, and an installed copy that still looks downloaded re-trips SmartScreen.</summary>
    private static void RemoveMarkOfTheWeb(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }

    /// <summary>Closes any earlier NvTune copy before replacing its files. Windows locks a running
    /// executable image, so an installed daemon or GUI left open would otherwise make the next
    /// save fail while the new settings were ready to apply.</summary>
    private static void StopOtherAppProcesses()
    {
        foreach (Process process in Process.GetProcessesByName("NvTuneApp"))
        {
            if (process.Id == Environment.ProcessId) continue;
            try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
            catch (Exception ex) { Diag.Log("关闭旧 NvTune 进程失败: " + ex.Message); }
        }
    }

    private static string Normalize(string? path)
        => string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
