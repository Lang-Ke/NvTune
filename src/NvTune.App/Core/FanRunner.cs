namespace SimpleNvidiaUndervolt;

/// <summary>
/// NVML fan control, plus the mapping between NVAPI's GPU enumeration (which keys every saved
/// document) and NVML's device list (which is what actually sets fan speed). The two enumerations
/// are not guaranteed to share an order on a multi-GPU machine, so devices are matched by model
/// name first and by index only as a fallback.
/// </summary>
internal static class FanRunner
{
    public const uint PolicyAuto = FanApi.PolicyAuto;
    public const uint PolicyManual = FanApi.PolicyManual;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, IntPtr> Map = new();
    private static bool _initialized;
    private static int _nvapiCount = -1;

    /// <summary>Opens NVML and pairs its devices with the NVAPI enumeration.</summary>
    public static void Initialize(int nvapiGpuCount)
    {
        lock (Gate)
        {
            if (_initialized && _nvapiCount == nvapiGpuCount) return;

            FanApi.Initialize();
            Map.Clear();
            _nvapiCount = nvapiGpuCount;

            int count = FanApi.GetDeviceCount();
            var nvml = new List<(int Index, IntPtr Handle, string Name)>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    IntPtr dev = FanApi.GetDevice((uint)i);
                    nvml.Add((i, dev, Normalize(FanApi.GetDeviceName(dev))));
                }
                catch (Exception ex)
                {
                    Diag.Log($"NVML 打开 GPU {i} 失败: {ex.Message}");
                }
            }

            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            var used = new HashSet<int>();
            for (int nvapi = 0; nvapi < gpus.Length; nvapi++)
            {
                string name = Normalize(NvApi.SafeFullName(gpus[nvapi]));
                int index = nvml.Where(d => !used.Contains(d.Index)
                    && d.Name.Length > 0 && name.Length > 0
                    && (d.Name == name || d.Name.Contains(name) || name.Contains(d.Name)))
                    .Select(d => d.Index)
                    .DefaultIfEmpty(-1)
                    .First();

                if (index < 0 && nvapi < count && !used.Contains(nvapi))
                {
                    index = nvapi;
                }

                if (index < 0)
                {
                    index = nvml.Where(d => !used.Contains(d.Index)).Select(d => d.Index).DefaultIfEmpty(-1).First();
                }

                if (index < 0)
                {
                    Diag.Log($"GPU {nvapi} 找不到对应的 NVML 设备。");
                    continue;
                }

                used.Add(index);
                Map[nvapi] = nvml.First(d => d.Index == index).Handle;
            }

            _initialized = true;
        }
    }

    private static string Normalize(string name)
        => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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

    public static uint PowerMilliwatts(IntPtr device) => FanApi.GetPowerMilliwatts(device);

    public static uint CoreMhz(IntPtr device) => FanApi.GetClockMhz(device, FanApi.ClockGraphics);

    public static uint Policy(IntPtr device, uint fan) => FanApi.GetFanPolicy(device, fan);

    public static void SetPolicy(IntPtr device, uint fan, uint policy) => FanApi.SetFanPolicy(device, fan, policy);

    public static void SetPercent(IntPtr device, uint fan, uint percent) => FanApi.SetFanPercent(device, fan, percent);

    /// <summary>Sets every fan of the GPU to a manual percentage.</summary>
    public static void SetAllFans(IntPtr device, uint percent)
    {
        uint fans = FanApi.GetFanCount(device);
        for (uint f = 0; f < fans; f++)
        {
            if (FanApi.GetFanPolicy(device, f) != PolicyManual)
            {
                FanApi.SetFanPolicy(device, f, PolicyManual);
            }

            FanApi.SetFanPercent(device, f, percent);
        }
    }

    public static void RestoreAuto(IntPtr device)
    {
        uint fans = FanApi.GetFanCount(device);
        for (uint f = 0; f < fans; f++)
        {
            FanApi.SetFanPolicy(device, f, PolicyAuto);
        }
    }

    /// <summary>
    /// Applies every stored fan setting once. Returns whether any setting existed and whether any of
    /// them is a temperature curve (which is what decides whether the caller has to keep polling).
    /// </summary>
    public static (bool AnySetting, bool AnyCurve) ApplyOnce()
    {
        bool anySetting = false;
        bool anyCurve = false;

        foreach (int index in FanState.StoredGpuIndexes())
        {
            if (!TryDevice(index, out IntPtr dev)) continue;

            FanState.FanSettings? settings = FanState.Load(index);
            if (settings is null) continue;

            int target;
            if (settings.ModeValue == FanState.Mode.Fixed)
            {
                target = settings.FixedPercent ?? 50;
            }
            else if (settings.ModeValue == FanState.Mode.Curve && settings.Curve is { } curve)
            {
                target = curve.FanPercentAt((int)FanApi.GetTemperatureC(dev));
                anyCurve = true;
            }
            else
            {
                continue;
            }

            anySetting = true;
            SetAllFans(dev, (uint)Math.Clamp(target, 0, 100));
        }

        return (anySetting, anyCurve);
    }
}
