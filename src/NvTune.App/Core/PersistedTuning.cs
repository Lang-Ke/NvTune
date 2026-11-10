namespace SimpleNvidiaUndervolt;

/// <summary>
/// The tuning document every persisting run stores for the logon task, which re-applies it with
/// <c>tune --apply-persisted</c>. Persisting the resolved offsets as data — rather than a command
/// line to re-plan — makes the logon re-apply exact: per-anchor deltas are temperature-independent,
/// so nothing is re-derived from a boot-time curve read. Stored per GPU as a JSON file in the
/// install directory's data subfolder (see <see cref="Persistence.DataFilePath"/>) — the same
/// document <c>--out-tuning-file</c> exports, so it can be read or copied directly — which is
/// under Program Files and thus admin-only writable, so the elevated logon re-apply never
/// consumes user-writable data.
/// </summary>
internal static class PersistedTuning
{
    private const string FileStem = "persisted-tuning";

    public static string FilePath(int gpuIndex) => Persistence.DataFilePath(FileStem, gpuIndex);

    /// <summary>The GPUs a tuning is stored for, ascending.</summary>
    public static IReadOnlyList<int> StoredGpuIndexes() => Persistence.StoredGpuIndexes(FileStem);

    /// <summary>What the logon re-apply runs against: every GPU with a stored tuning. Nothing
    /// stored must fail loudly — silence would read as an applied tuning.</summary>
    public static GpuSelector StoredSelector()
    {
        IReadOnlyList<int> stored = StoredGpuIndexes();
        return stored.Count > 0 ? GpuSelector.Stored(stored) : throw NothingStored(0);
    }

    /// <summary>Stores the document, replacing any previous one for the GPU. Requires administrator
    /// (the install directory); the environment refusing the write is reported as the anticipated
    /// failure it is.</summary>
    public static void Save(TuningDoc doc, int gpuIndex)
        => TuningDocuments.WriteFile(FilePath(gpuIndex), TuningDocuments.Render(doc), "the persisted tuning");

    /// <summary>The stored document. An <c>--apply-persisted</c> run with nothing (usable) stored
    /// must fail loudly — it is the logon re-apply, and silence would read as an applied tuning.</summary>
    public static TuningDoc Load(int gpuIndex)
    {
        if (!File.Exists(FilePath(gpuIndex)))
        {
            throw NothingStored(gpuIndex);
        }

        return TuningDocuments.ReadTuningFile(FilePath(gpuIndex));
    }

    private static CliError NothingStored(int gpuIndex)
        => new($"No persisted tuning is stored at {FilePath(gpuIndex)} - nothing to apply. "
               + "Re-run your tune command to persist one.");

    /// <summary>One phrase for status's logon line: each stored document's capture date when it
    /// reads, else the inconsistency — an apply-persisted task with nothing to apply fails at the
    /// next logon, and status must say so rather than "yes". GPUs are named only past the
    /// single-GPU case.</summary>
    public static string DescribeForStatus()
    {
        IReadOnlyList<int> stored = StoredGpuIndexes();
        if (stored.Count == 0)
        {
            return "which is missing - re-run your tune command";
        }

        return stored is [0]
            ? Describe(0)
            : string.Join("; ", stored.Select(index => $"GPU {index}: {Describe(index)}"));
    }

    private static string Describe(int gpuIndex)
    {
        try
        {
            return TuningDocuments.DescribeCapture(Load(gpuIndex).SavedAt);
        }
        catch (CliError)
        {
            return "unreadable - re-run your tune command";
        }
    }

    /// <summary>Deletes the GPU's stored document, returning whether one existed. Throws when the
    /// delete itself fails, so <c>clear</c> doesn't report persisted state as gone when it isn't.</summary>
    public static bool Remove(int gpuIndex)
    {
        if (!File.Exists(FilePath(gpuIndex)))
        {
            return false;
        }

        File.Delete(FilePath(gpuIndex));
        return true;
    }
}
