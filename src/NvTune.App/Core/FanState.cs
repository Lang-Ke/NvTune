using System.Text.Json;

namespace NvTune;

/// <summary>The stored fan control state for one GPU: mode (auto/fixed/curve) and the mode's data.
/// Stored as JSON beside the executable so the daemon and the GUI agree without a database.</summary>
internal static class FanState
{
    private const string FileStem = "fan-control";

    public enum Mode { Auto = 0, Fixed = 1, Curve = 2 }

    public sealed record FanSettings(int Mode, int? FixedPercent, int[][]? CurvePoints, string? GpuUuid = null)
    {
        public Mode ModeValue => (Mode)Mode;

        public FanCurve? Curve => CurvePoints is { Length: > 0 } pts
            ? new FanCurve(pts.Select(p => (p[0], p[1])).ToArray())
            : null;

        public void Validate()
        {
            if (!Enum.IsDefined(ModeValue)) throw new CliError("未知的风扇模式。");
            if (ModeValue == FanState.Mode.Fixed && FixedPercent is not (>= 0 and <= 100))
                throw new CliError("固定风扇转速必须在 0–100% 之间。");
            if (ModeValue == FanState.Mode.Curve)
            {
                if (CurvePoints is not { Length: >= 2 and <= 12 }
                    || CurvePoints.Any(p => p is not { Length: 2 } || p[0] is < 20 or > 100 || p[1] is < 0 or > 100)
                    || CurvePoints.Select(p => p[0]).Distinct().Count() != CurvePoints.Length)
                    throw new CliError("风扇曲线需要 2–12 个温度不重复的有效控制点。");
            }
        }
    }

    public static string FilePath(int gpuIndex) => Persistence.DataFilePath(FileStem, gpuIndex);

    public static IReadOnlyList<int> StoredGpuIndexes() => Persistence.StoredGpuIndexes(FileStem);

    public static FanSettings? Load(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (!File.Exists(path)) return null;

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Diag.Log($"读取风扇设置 GPU {gpuIndex} 失败: {ex.Message}");
            throw new CliError($"GPU {gpuIndex} 风扇配置损坏: {ex.Message}");
        }
    }

    internal static FanSettings Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        int mode = doc.TryGetProperty("mode", out var m) ? m.GetInt32() : 0;
        int? fixedPct = doc.TryGetProperty("fixedPercent", out var f) && f.ValueKind == JsonValueKind.Number
            ? f.GetInt32()
            : null;
        int[][]? curve = doc.TryGetProperty("curve", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
                .Select(p => p.EnumerateArray().Select(v => v.GetInt32()).ToArray())
                .ToArray()
            : null;
        string? uuid = doc.TryGetProperty("gpuUuid", out var u) ? u.GetString() : null;
        var settings = new FanSettings(mode, fixedPct, curve, uuid);
        settings.Validate();
        return settings;
    }

    public static void Save(FanSettings settings, int gpuIndex)
    {
        settings.Validate();
        var obj = new Dictionary<string, object?> { ["mode"] = settings.Mode };
        if (settings.GpuUuid is { } uuid) obj["gpuUuid"] = uuid;
        if (settings.FixedPercent is { } fp) obj["fixedPercent"] = fp;
        if (settings.CurvePoints is { } cp) obj["curve"] = cp;
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        TuningDocuments.WriteFile(FilePath(gpuIndex), json, "风扇配置");
    }

    public static void Delete(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (File.Exists(path)) File.Delete(path);
    }
}
