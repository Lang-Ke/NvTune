using System.Text.Json;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// <summary>
/// The undervolt values the user last committed for one GPU, kept purely so the panel reopens on
/// them instead of on a computed default. The authoritative document is <see cref="PersistedTuning"/>;
/// this file only records the requested voltage/clock/memory offset, which a tuning document cannot
/// express (it holds per-anchor offsets, not the request).
/// </summary>
internal static class UndervoltUiState
{
    private const string FileStem = "undervolt-ui";

    public sealed record Values(int Mv, int Mhz, int MemMhz);

    public static string FilePath(int gpuIndex) => Persistence.DataFilePath(FileStem, gpuIndex);

    public static Values? Load(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            if (!doc.TryGetProperty("mv", out var mv) || !doc.TryGetProperty("mhz", out var mhz))
            {
                return null;
            }

            int mem = doc.TryGetProperty("memMhz", out var memValue) && memValue.ValueKind == JsonValueKind.Number
                ? memValue.GetInt32()
                : 0;
            return new Values(mv.GetInt32(), mhz.GetInt32(), mem);
        }
        catch (Exception ex)
        {
            Diag.Log($"读取降压界面参数 GPU {gpuIndex} 失败: {ex.Message}");
            return null;
        }
    }

    public static void Save(Values values, int gpuIndex)
    {
        var obj = new Dictionary<string, object?>
        {
            ["mv"] = values.Mv,
            ["mhz"] = values.Mhz,
            ["memMhz"] = values.MemMhz,
        };
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath(gpuIndex), json);
    }

    public static void Delete(int gpuIndex)
    {
        string path = FilePath(gpuIndex);
        if (File.Exists(path)) File.Delete(path);
    }
}
