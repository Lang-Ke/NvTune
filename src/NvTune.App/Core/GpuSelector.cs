using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Which of the enumerated NVIDIA GPUs a command runs against, from <c>--gpu</c>: one index into
/// <see cref="NvApi.EnumeratePhysicalGpus"/> (0 when the flag is absent), or <c>all</c> of them in
/// turn. Every GPU-bound command takes it. The logon re-apply, which names no GPU, instead covers
/// every GPU a tuning was persisted for (see <see cref="PersistedTuning.StoredSelector"/>).
/// </summary>
internal sealed class GpuSelector
{
    public const string Flag = "--gpu";

    public static readonly GpuSelector Default = new(new[] { 0 }, arg: null, fromStore: false);

    /// <summary>The selected indexes, ascending, or null for every enumerated GPU.</summary>
    public IReadOnlyList<int>? Indexes { get; }

    /// <summary>The flag's value as typed, re-emitted into a saved shortcut; null when the flag was
    /// absent (the index-0 default) or the selection isn't from the command line.</summary>
    private readonly string? _arg;

    /// <summary>Whether the indexes are the persisted set rather than a request — what decides how
    /// a missing GPU is explained.</summary>
    private readonly bool _fromStore;

    private GpuSelector(IReadOnlyList<int>? indexes, string? arg, bool fromStore)
    {
        Indexes = indexes;
        _arg = arg;
        _fromStore = fromStore;
    }

    /// <summary>Whether <c>--gpu</c> was given, as opposed to the index-0 default.</summary>
    public bool Given => _arg is not null;

    public bool IsAll => Indexes is null;

    /// <summary>The GPUs a tuning is persisted for — what the logon re-apply runs against.</summary>
    public static GpuSelector Stored(IReadOnlyList<int> indexes) => new(indexes, arg: null, fromStore: true);

    public static GpuSelector Parse(Args.Parsed parsed)
    {
        if (parsed.Value(Flag) is not { } raw)
        {
            return Default;
        }

        if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "all", fromStore: false);
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            throw new CliError($"{Flag} takes a GPU index (0, 1, ...) or 'all'.");
        }

        return new(new[] { index }, raw, fromStore: false);
    }

    /// <summary>The indexes to run against, in order, on a machine with <paramref name="gpuCount"/>
    /// GPUs. Not range-checked: the caller reports an index past the enumeration with
    /// <see cref="MissingGpu"/> — as that GPU's own failure when others are selected too.</summary>
    public IReadOnlyList<int> IndexesOn(int gpuCount)
        => Indexes ?? Enumerable.Range(0, gpuCount).ToArray();

    /// <summary>The error for a selected index past the enumeration. A stored index with no card
    /// means a persisted tuning outlived its GPU, which <c>clear</c> drops.</summary>
    public CliError MissingGpu(int index, int gpuCount)
        => new($"GPU {index} doesn't exist: {gpuCount} NVIDIA GPU(s) found (indexes 0-{gpuCount - 1})."
               + (_fromStore ? $" Its persisted tuning is dropped with 'clear {Flag} {index}'." : string.Empty));

    /// <summary>Rejects <c>all</c> alongside an option naming one output file: an export can't
    /// come from several GPUs.</summary>
    public void RequireOneForExport(string flag, string? file)
    {
        if (IsAll && file is not null)
        {
            throw new CliError($"{flag} exports one GPU's document - pass {Flag} <index> instead of 'all'.");
        }
    }

    /// <summary>The flag for a saved shortcut's command line: nothing for the default.</summary>
    public string[] ToArgs() => _arg is null ? Array.Empty<string>() : new[] { Flag, _arg };

    /// <summary>The selection as a name token: "GPU 1", "all GPUs".</summary>
    public string Describe()
        => Indexes is null ? "all GPUs" : "GPU " + string.Join(", ", Indexes);
}

/// <summary>One GPU a command runs against: its NVAPI handle, its index in the enumeration and the
/// enumeration's size. The index keys the GPU's own store files (see
/// <see cref="Persistence.DataFilePath"/>) and, on a multi-GPU machine, names the card.</summary>
internal readonly record struct SelectedGpu(IntPtr Handle, int Index, int Count)
{
    /// <summary>The card's name for headers, with its index once the machine has more than one GPU.</summary>
    public string Label
        => Count > 1 ? $"{NvApi.SafeFullName(Handle)} (GPU {Index} of {Count})" : NvApi.SafeFullName(Handle);
}
