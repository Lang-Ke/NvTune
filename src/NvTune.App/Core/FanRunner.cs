namespace SimpleNvidiaUndervolt;

/// <summary>
/// NVML fan control, plus the mapping between NVAPI's GPU enumeration (which keys every saved
/// document) and NVML's device list (which is what actually sets fan speed). The two enumerations
/// are not guaranteed to share an order on a multi-GPU machine, so devices are matched by
/// physical PCI bus ID, refusing ambiguous matches.
/// </summary>
internal static class FanRunner
{
    public const uint PolicyAuto = FanApi.PolicyAuto;
    public const uint PolicyManual = FanApi.PolicyManual;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, IntPtr> Map = new();
    public static int[] MappedIndexes() { lock (Gate) return Map.Keys.ToArray(); }
    /// <summary>Rebuild mappings from physical PCI bus IDs; never guess by name or enumeration.
    /// Ambiguous bus IDs (e.g. multiple PCI domains) are deliberately left unmapped.</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            FanApi.Initialize();
            var devices = new List<(int Index, uint Bus, IntPtr Handle)>();
            bool complete = true;
            int count = FanApi.GetDeviceCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    IntPtr dev = FanApi.GetDevice((uint)i);
                    devices.Add((i, FanApi.GetBusId(dev), dev));
                }
                catch (Exception ex) { complete = false; Diag.Log($"NVML 打开 GPU {i} 失败: {ex.Message}"); }
            }
            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            var buses = new List<(int Index, uint Bus)>();
            for (int i = 0; i < gpus.Length; i++)
            {
                try { buses.Add((i, NvApi.GetBusId(gpus[i]))); }
                catch (Exception ex) { complete = false; Diag.Log($"读取 GPU {i} 总线失败: {ex.Message}"); }
            }
            Map.Clear();
            if (!complete) throw new CliError("显卡 PCI 地址枚举不完整，无法核实唯一匹配，将重试。");
            foreach (var (index, nvmlIndex) in GpuBusMapping.Match(buses, devices.Select(d => (d.Index, d.Bus))))
                Map[index] = devices.Single(d => d.Index == nvmlIndex).Handle;
            foreach (var (index, _) in buses.Where(b => !Map.ContainsKey(b.Index)))
                Diag.Log($"GPU {index} 无法唯一匹配 NVML 设备，已禁用该卡的风扇写入。");
        }
    }

    public static FanController CreateController() => new(new DriverBackend(), Diag.Log);

    private sealed class DriverBackend : IFanBackend
    {
        public int TemperatureC(int index) => checked((int)FanRunner.TemperatureC(Device(index)));
        public void SetPercent(int index, int percent) => SetAllFans(Device(index), (uint)Math.Clamp(percent, 0, 100));
        public void RestoreAuto(int index) => FanRunner.RestoreAuto(Device(index));
    }

    public static string Uuid(int index) => FanApi.GetUuid(Device(index));

    public static string Uuid(IntPtr gpu)
    {
        uint bus = NvApi.GetBusId(gpu);
        var gpus = NvApi.EnumeratePhysicalGpus();
        int index = Array.FindIndex(gpus, g => g == gpu);
        if (index < 0) throw new CliError($"PCI 总线 {bus} 的显卡已断开。");
        return Uuid(index);
    }

    public static FanState.FanSettings? LoadSaved(int index)
    {
        var saved = FanState.Load(index);
        if (saved is not null) GpuBinding.Require(saved.GpuUuid, Uuid(index));
        return saved;
    }
    public static bool TryDevice(int nvapiIndex, out IntPtr device)
    {
        lock (Gate)
        {
            return Map.TryGetValue(nvapiIndex, out device);
        }
    }

    public static IntPtr Device(int nvapiIndex)
        => TryDevice(nvapiIndex, out IntPtr dev)
            ? dev
            : throw new CliError($"风扇接口找不到 GPU {nvapiIndex}，请更新显卡驱动后重试。");

    public static uint FanCount(IntPtr device) => FanApi.GetFanCount(device);

    public static uint TemperatureC(IntPtr device) => FanApi.GetTemperatureC(device);

    public static uint FanPercent(IntPtr device, uint fan) => FanApi.GetFanPercent(device, fan);

    public static IReadOnlyList<FanTelemetry> ReadFanTelemetry(IntPtr device)
    {
        var readings = new List<FanTelemetry>();
        for (uint fan = 0; fan < FanCount(device); fan++)
        {
            uint? percent;
            try { percent = FanApi.GetFanPercent(device, fan); } catch (CliError) { percent = null; }
            readings.Add(new FanTelemetry(fan, percent, FanApi.GetTargetFanPercent(device, fan),
                FanApi.GetFanRpm(device, fan), FanApi.TryGetFanPolicy(device, fan)));
        }
        return readings;
    }

    public static uint PowerMilliwatts(IntPtr device) => FanApi.GetPowerMilliwatts(device);

    public static uint CoreMhz(IntPtr device) => FanApi.GetClockMhz(device, FanApi.ClockGraphics);

    public static uint Policy(IntPtr device, uint fan) => FanApi.GetFanPolicy(device, fan);

    public static void SetPolicy(IntPtr device, uint fan, uint policy) => FanApi.SetFanPolicy(device, fan, policy);

    public static void SetPercent(IntPtr device, uint fan, uint percent) => FanApi.SetFanPercent(device, fan, percent);

    /// <summary>Sets every fan of the GPU to a manual percentage.</summary>
    public static void SetAllFans(IntPtr device, uint percent)
    {
        uint fans = FanApi.GetFanCount(device);
        if (fans == 0) throw new CliError("此显卡没有可控制的风扇。");
        int effective = FanTelemetry.EffectivePercent((int)percent, FanApi.GetFanRange(device));
        var errors = new List<Exception>();
        for (uint f = 0; f < fans; f++)
        {
            // NVML's setter itself switches this fan to manual policy. Do not require a
            // separate policy getter that older driver versions may not export.
            try
            {
                FanApi.SetFanPercent(device, f, (uint)effective);
                // Verify the accepted target, never compare against a potentially lagging or
                // differently normalized percentage telemetry value.
                FanTelemetry.RequireAccepted(effective, FanApi.GetTargetFanPercent(device, f));
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("部分风扇转速写入失败。", errors);
    }

    public static void RestoreAuto(IntPtr device)
    {
        uint fans = FanApi.GetFanCount(device);
        var errors = new List<Exception>();
        for (uint f = 0; f < fans; f++)
        {
            try { FanApi.RestoreDefaultFan(device, f); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("部分风扇无法恢复自动控制。", errors);
    }

}
