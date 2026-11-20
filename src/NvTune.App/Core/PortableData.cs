using System.Text.Json;

namespace NvTune;

/// <summary>One-time configuration import. Explicit paths keep migration tests away from
/// the user's installation. Runtime records and arbitrary files are never imported.</summary>
internal static class PortableData
{
    internal const string MarkerName = "portable-state.json";
    internal const string JournalName = "portable-import.json";
    private static readonly string[] ConfigurationStems =
        { "persisted-tuning", "undervolt-ui", "fan-control", "reference-curve" };

    internal static string DataDirectory(string executablePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!, "data");

    internal static int Initialize(string dataDirectory, string legacyDataDirectory)
    {
        string destination = Path.GetFullPath(dataDirectory);
        string legacy = Path.GetFullPath(legacyDataDirectory);
        Directory.CreateDirectory(destination);
        string marker = Path.Combine(destination, MarkerName);
        string journalPath = Path.Combine(destination, JournalName);
        if (File.Exists(journalPath))
        {
            // A previous process may have stopped between individual file moves. Recover this
            // snapshot before checking local settings, which may be only partly imported.
            ImportJournal pending = ReadJournal(journalPath);
            ValidateStaging(destination, pending.Staging);
            if (!File.Exists(marker)) return Commit(destination, pending);
            Cleanup(destination, pending);
            return 0;
        }
        if (File.Exists(marker)) return 0;

        bool hasLocalSettings = Directory.EnumerateFiles(destination, "*.json")
            .Any(file => IsConfiguration(Path.GetFileName(file)));
        string[] sources = !hasLocalSettings && !Path.TrimEndingDirectorySeparator(destination)
            .Equals(Path.TrimEndingDirectorySeparator(legacy), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(legacy)
            ? Directory.GetFiles(legacy, "*.json").Where(file => IsConfiguration(Path.GetFileName(file))).ToArray()
            : Array.Empty<string>();

        var journal = new ImportJournal(".import-" + Guid.NewGuid().ToString("N"),
            sources.Select(Path.GetFileName).Select(name => name!).ToArray());
        string staging = ValidateStaging(destination, journal.Staging);
        bool published = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (string file in sources) File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
            // Publish only after every source is copied. Its atomic rename precedes every
            // configuration move, so an interrupted commit always has a recovery record.
            string stagedJournal = Path.Combine(staging, "journal.tmp");
            File.WriteAllText(stagedJournal, JsonSerializer.Serialize(new
            {
                version = 1, staging = journal.Staging, files = journal.Files,
            }));
            File.Move(stagedJournal, journalPath, overwrite: false);
            published = true;
            return Commit(destination, journal);
        }
        catch
        {
            if (!published) Cleanup(destination, journal, removeJournal: false);
            throw;
        }
    }

    private static int Commit(string destination, ImportJournal journal)
    {
        string staging = ValidateStaging(destination, journal.Staging);
        var committed = new List<string>();
        try
        {
            foreach (string name in journal.Files)
            {
                string target = Path.Combine(destination, name);
                // Existing targets may already have been moved by the interrupted process.
                // They may also have been edited afterwards; never overwrite either case.
                if (File.Exists(target)) continue;
                File.Move(Path.Combine(staging, name), target, overwrite: false);
                committed.Add(name);
            }
            Directory.CreateDirectory(staging);
            string stagedMarker = Path.Combine(staging, MarkerName);
            File.WriteAllText(stagedMarker, "{\"version\":1}" + Environment.NewLine);
            File.Move(stagedMarker, Path.Combine(destination, MarkerName), overwrite: false);
        }
        catch
        {
            // Restore this attempt's moves to the snapshot where possible. Keep the journal
            // so a later attempt can also recover files committed before this process started.
            foreach (string name in committed.AsEnumerable().Reverse())
            {
                try { File.Move(Path.Combine(destination, name), Path.Combine(staging, name), overwrite: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        Cleanup(destination, journal);
        return committed.Count;
    }

    private static ImportJournal ReadJournal(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw InvalidJournal();
            string staging = root.GetProperty("staging").GetString() ?? throw InvalidJournal();
            string[] files = root.GetProperty("files").EnumerateArray()
                .Select(file => file.GetString() ?? throw InvalidJournal()).ToArray();
            if (files.Any(name => Path.GetFileName(name) != name || !IsConfiguration(name))
                || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
                throw InvalidJournal();
            return new ImportJournal(staging, files);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw InvalidJournal(ex);
        }
    }

    private static string ValidateStaging(string destination, string name)
    {
        const string prefix = ".import-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 32
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) throw InvalidJournal();
        string staging = Path.Combine(destination, name);
        if (Directory.Exists(staging) && (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
            throw InvalidJournal();
        return staging;
    }

    private static void Cleanup(string destination, ImportJournal journal, bool removeJournal = true)
    {
        try
        {
            string staging = ValidateStaging(destination, journal.Staging);
            if (Directory.Exists(staging))
            {
                // Even a journal read from disk only authorizes these known direct children.
                // Do not recursively remove a folder supplied by a corrupted record.
                foreach (string name in journal.Files) File.Delete(Path.Combine(staging, name));
                File.Delete(Path.Combine(staging, MarkerName));
                File.Delete(Path.Combine(staging, "journal.tmp"));
                Directory.Delete(staging, recursive: false);
            }
            if (removeJournal) File.Delete(Path.Combine(destination, JournalName));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static InvalidDataException InvalidJournal(Exception? inner = null)
        => new("便携配置导入记录无效，请保留 data 文件夹并检查 portable-import.json。", inner);

    private sealed record ImportJournal(string Staging, string[] Files);

    private static bool IsConfiguration(string fileName)
        => ConfigurationStems.Any(stem => Persistence.DataFileGpuIndex(stem, fileName) is not null);
}
