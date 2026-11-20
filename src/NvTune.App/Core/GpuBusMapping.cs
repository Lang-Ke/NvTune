namespace SimpleNvidiaUndervolt;

internal static class GpuBusMapping
{
    public static IReadOnlyDictionary<int, int> Match(IEnumerable<(int Index, uint Bus)> nvapi,
        IEnumerable<(int Index, uint Bus)> nvml)
    {
        var left = nvapi.ToArray();
        var right = nvml.ToArray();
        var matches = new Dictionary<int, int>();
        foreach (var gpu in left)
        {
            var candidates = right.Where(d => d.Bus == gpu.Bus).ToArray();
            if (left.Count(d => d.Bus == gpu.Bus) == 1 && candidates.Length == 1)
                matches.Add(gpu.Index, candidates[0].Index);
        }
        return matches;
    }
}

internal static class GpuBinding
{
    public static void Require(string? savedUuid, string actualUuid)
    {
        if (string.IsNullOrWhiteSpace(savedUuid))
            throw new CliError("旧配置未绑定显卡 UUID，请核对参数并重新写入配置。");
        if (!string.Equals(savedUuid, actualUuid, StringComparison.OrdinalIgnoreCase))
            throw new CliError("配置绑定的显卡 UUID 与当前设备不符，已拒绝写入。请重新选择显卡并保存配置。");
    }
}
