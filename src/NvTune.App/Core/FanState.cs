using System.Text.Json;

namespace SimpleNvidiaUndervolt;

/// <summary>The stored fan control state for one GPU: mode (auto/fixed/curve) and the mode's data.
/// Stored as JSON beside the executable so the daemon and the GUI agree without a database.</summary>
internal static class FanState
{
    private const string FileStem = "fan-control";

    public enum Mode { Auto = 0, Fixed = 1, Curve = 2 }

    public sealed record FanSettings(int Mode, int? FixedPercent, int[][]? CurvePoints)
    {
        public Mode ModeValue => (Mode)Mode;

        public FanCurve? Curve => CurvePoints is { Length: > 0 } pts
            ? new FanCurve(pts.Select(p => (p[0], p[1])).ToArray())
            : null;
    }

    public static string FilePath(int gpuIndex) => Persistence.DataFilePath(FileStem, gpuIndex);

    public static IReadOnlyList<int> StoredGpuIndexes() => Persistence.StoredGpuIndexes(FileStem);

    public static FanSettings? Load(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            int mode = doc.TryGetProperty("mode", out var m) ? m.GetInt32() : 0;
            int? fixedPct = doc.TryGetProperty("fixedPercent", out var f) && f.ValueKind == JsonValueKind.Number
                ? f.GetInt32()
                : null;
            int[][]? curve = doc.TryGetProperty("curve", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray()
                    .Select(p => p.EnumerateArray().Select(v => v.GetInt32()).ToArray())
                    .ToArray()
                : null;
            return new FanSettings(mode, fixedPct, curve);
        }
        catch (Exception ex)
        {
            Diag.Log($"读取风扇设置 GPU {gpuIndex} 失败: {ex.Message}");
            return null;
        }
    }

    public static void Save(FanSettings settings, int gpuIndex)
    {
        var obj = new Dictionary<string, object?> { ["mode"] = settings.Mode };
        if (settings.FixedPercent is { } fp) obj["fixedPercent"] = fp;
        if (settings.CurvePoints is { } cp) obj["curve"] = cp;
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath(gpuIndex), json);
    }

    public static void Delete(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (File.Exists(path)) File.Delete(path);
    }
}