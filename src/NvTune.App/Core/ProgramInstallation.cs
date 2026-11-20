namespace NvTune;

/// <summary>Copies only the files belonging to the active deployment layout. Paths are supplied
/// by the caller so installation and migration can be checked without touching Program Files.</summary>
internal static class ProgramInstallation
{
    private static readonly string[] MultiFileNames =
    {
        "NvTuneApp.exe", "NvTuneApp.dll", "NvTuneApp.deps.json", "NvTuneApp.runtimeconfig.json"
    };

    private static readonly string[] LegacyCompanions =
    {
        "NvTuneApp.dll", "NvTuneApp.deps.json", "NvTuneApp.runtimeconfig.json", "NvTuneApp.pdb", "启动器.exe"
    };

    internal static void Install(string source, string target, bool singleFile,
        Action<string>? onCopied = null, Action<string>? log = null)
    {
        string sourcePath = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string targetPath = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

        string[] required = singleFile ? new[] { "NvTuneApp.exe" } : MultiFileNames;
        // Validate the entire layout before overwriting anything or removing old companions.
        foreach (string name in required)
        {
            string file = Path.Combine(sourcePath, name);
            if (!File.Exists(file)) throw new FileNotFoundException($"安装文件不完整，缺少 {name}。", file);
        }

        Directory.CreateDirectory(targetPath);
        foreach (string name in required)
        {
            string destination = Path.Combine(targetPath, name);
            File.Copy(Path.Combine(sourcePath, name), destination, overwrite: true);
            onCopied?.Invoke(destination);
        }

        // Cleanup begins only after every required file has been copied. A single-file update
        // may be unpacked over an older portable build, so stray source companions are ignored.
        foreach (string name in LegacyCompanions)
        {
            if (required.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!singleFile && File.Exists(Path.Combine(sourcePath, name))) continue;
            File.Delete(Path.Combine(targetPath, name));
        }

        // Migrate earlier portable JSON configurations without replacing installed settings.
        string oldData = Path.Combine(sourcePath, "data");
        if (!Directory.Exists(oldData)) return;
        string newData = Path.Combine(targetPath, "data");
        Directory.CreateDirectory(newData);
        foreach (string file in Directory.GetFiles(oldData, "*.json"))
        {
            string destination = Path.Combine(newData, Path.GetFileName(file));
            if (File.Exists(destination)) continue;
            try { File.Copy(file, destination); }
            catch (Exception ex) { log?.Invoke("迁移配置失败: " + ex.Message); }
        }
    }
}
