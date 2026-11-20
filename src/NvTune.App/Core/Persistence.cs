using System.Globalization;

namespace NvTune;

/// <summary>
/// Installed and portable builds use distinct data locations, selected at build time. Runtime
/// arguments or executable renaming cannot change which configuration the daemon consumes.
/// </summary>
internal static class Persistence
{
#if PORTABLE
    public static bool IsPortable => true;
#else
    public static bool IsPortable => false;
#endif
    public static string Edition => IsPortable ? "portable" : "installed";
    internal static string LegacyInstallDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NvTune");
    internal static string LegacyDataDir => Path.Combine(LegacyInstallDir, "data");
    internal static string LegacyExecutablePath => Path.Combine(LegacyInstallDir, "NvTuneApp.exe");
    internal static string LegacyLockPath(string name) => Path.Combine(LegacyDataDir, name);
    internal static string PortableDataDir => PortableData.DataDirectory(Product.ExecutablePath());
    internal static string ActiveDataDir => IsPortable ? PortableDataDir : LegacyDataDir;
    public static string InstallDir() => LegacyInstallDir;
    public static string InstallExePath() => LegacyExecutablePath;

    public static bool RunningInstalledCopy()
        => string.Equals(Path.GetFullPath(Product.ExecutablePath()), InstallExePath(), StringComparison.OrdinalIgnoreCase);

    // A bundled managed assembly has no separate location. This is only a layout check;
    // executable and installation paths continue to use ProcessPath and BaseDirectory.
#pragma warning disable IL3000
    internal static bool IsSingleFile => typeof(Persistence).Assembly.Location.Length == 0;
#pragma warning restore IL3000

    public static string DataDir()
    {
        string dir = ActiveDataDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void InitializePortableData()
    {
        if (!IsPortable) return;
        try
        {
            int imported = PortableData.Initialize(PortableDataDir, LegacyDataDir);
            if (imported > 0) Diag.Log($"已从旧安装目录导入 {imported} 个配置文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliError($"无法准备便携配置目录 {PortableDataDir}。请将程序放到可写的文件夹后重试。{ex.Message}");
        }
    }

    public static string DataFilePath(string stem, int gpuIndex)
        => Path.Combine(DataDir(), gpuIndex == 0 ? $"{stem}.json" : $"{stem}.gpu{gpuIndex}.json");

    public static IReadOnlyList<int> StoredGpuIndexes(string stem)
    {
        string dir = ActiveDataDir;
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

    public static string? EnsureInstalled()
    {
        if (IsPortable || RunningInstalledCopy()) return null;
        string target = InstallDir();
        ProgramInstallation.Install(AppContext.BaseDirectory, target, IsSingleFile, RemoveMarkOfTheWeb, Diag.Log);
        Diag.Log($"已安装到 {target}");
        return target;
    }

    private static void RemoveMarkOfTheWeb(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }
}
