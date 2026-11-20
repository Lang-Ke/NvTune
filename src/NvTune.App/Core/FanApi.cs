using System.Runtime.InteropServices;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>Minimal NVML interop for fan control: read/set fan percentage and control policy per
/// physical GPU. NVML ships with the NVIDIA driver as nvml.dll, so no extra runtime is needed.</summary>
internal static class FanApi
{
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", SetLastError = false)]
    private static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", SetLastError = false)]
    private static extern int NvmlShutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", SetLastError = false)]
    private static extern int NvmlGetHandle(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3", SetLastError = false)]
    private static extern int NvmlGetPciInfo(IntPtr device, out PciInfo info);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi, SetLastError = false)]
    private static extern int NvmlGetUuid(IntPtr device, StringBuilder uuid, uint length);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2", SetLastError = false)]
    private static extern int NvmlSetDefaultFanSpeed(IntPtr device, uint fan);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", SetLastError = false)]
    private static extern int NvmlGetCount(out uint count);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans", SetLastError = false)]
    private static extern int NvmlGetNumFans(IntPtr device, out uint count);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2", SetLastError = false)]
    private static extern int NvmlGetFanSpeed(IntPtr device, uint fan, out uint percent);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTargetFanSpeed", SetLastError = false)]
    private static extern int NvmlGetTargetFanSpeed(IntPtr device, uint fan, out uint percent);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMinMaxFanSpeed", SetLastError = false)]
    private static extern int NvmlGetMinMaxFanSpeed(IntPtr device, out uint minimum, out uint maximum);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM", SetLastError = false)]
    private static extern int NvmlGetFanSpeedRpm(IntPtr device, ref FanSpeedInfo info);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetFanSpeed_v2", SetLastError = false)]
    private static extern int NvmlSetFanSpeed(IntPtr device, uint fan, uint percent);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanControlPolicy_v2", SetLastError = false)]
    private static extern int NvmlGetFanPolicy(IntPtr device, uint fan, out uint policy);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetFanControlPolicy", SetLastError = false)]
    private static extern int NvmlSetFanPolicy(IntPtr device, uint fan, uint policy);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", SetLastError = false)]
    private static extern int NvmlGetTemperature(IntPtr device, uint sensor, out uint tempC);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage", SetLastError = false)]
    private static extern int NvmlGetPower(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo", SetLastError = false)]
    private static extern int NvmlGetClock(IntPtr device, uint type, out uint mhz);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi, SetLastError = false)]
    private static extern int NvmlGetName(IntPtr device, StringBuilder name, uint length);

    public const uint ClockGraphics = 0;
    public const uint PolicyAuto = 0;
    public const uint PolicyManual = 1;

    private static bool _initialized;

    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo
    {
        public uint Version, Fan, Speed;
    }

    public static uint? GetFanRpm(IntPtr device, uint fan)
    {
        // NVML version encodes the revision in bits 24..31 (unlike NVAPI's bit 16).
        var info = new FanSpeedInfo { Version = (uint)Marshal.SizeOf<FanSpeedInfo>() | (1u << 24), Fan = fan };
        try { return NvmlGetFanSpeedRpm(device, ref info) == 0 ? info.Speed : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static uint? TryGetFanPolicy(IntPtr device, uint fan)
    {
        try { return NvmlGetFanPolicy(device, fan, out uint policy) == 0 ? policy : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain, Bus, Device, DeviceId, SubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    public static uint GetBusId(IntPtr device)
    {
        int rc = NvmlGetPciInfo(device, out var pci);
        if (rc != 0) throw new CliError($"NVML 读取 PCI 地址失败 (错误码 {rc})。");
        return pci.Bus;
    }

    public static string GetUuid(IntPtr device)
    {
        var uuid = new StringBuilder(96);
        int rc = NvmlGetUuid(device, uuid, 96);
        if (rc != 0 || uuid.Length == 0) throw new CliError($"NVML 读取显卡 UUID 失败 (错误码 {rc})。");
        return uuid.ToString();
    }

    public static void RestoreDefaultFan(IntPtr device, uint fan)
    {
        int rc = NvmlSetDefaultFanSpeed(device, fan);
        if (rc != 0) throw new CliError($"恢复风扇 {fan} 自动控制失败 (错误码 {rc})。");
    }

    public static void Initialize()
    {
        if (_initialized) return;
        int rc = NvmlInit();
        if (rc != 0) throw new CliError($"NVML 初始化失败 (错误码 {rc})。请确认已安装 NVIDIA 显卡驱动。");
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (_initialized) { NvmlShutdown(); _initialized = false; }
    }

    public static IntPtr GetDevice(uint index)
    {
        int rc = NvmlGetHandle(index, out IntPtr device);
        if (rc != 0) throw new CliError($"NVML 获取 GPU {index} 句柄失败 (错误码 {rc})。");
        return device;
    }

    /// <summary>How many NVIDIA devices NVML can see.</summary>
    public static int GetDeviceCount()
    {
        int rc = NvmlGetCount(out uint count);
        if (rc != 0) throw new CliError($"NVML 枚举显卡失败 (错误码 {rc})。");
        return checked((int)count);
    }

    public static uint GetFanCount(IntPtr device)
    {
        int rc = NvmlGetNumFans(device, out uint count);
        if (rc != 0) throw new CliError($"读取风扇数量失败 (错误码 {rc})。");
        return count;
    }

    public static uint GetFanPercent(IntPtr device, uint fan)
    {
        int rc = NvmlGetFanSpeed(device, fan, out uint pct);
        if (rc != 0) throw new CliError($"读取风扇 {fan} 百分比失败 (错误码 {rc})。");
        return pct;
    }

    public static uint? GetTargetFanPercent(IntPtr device, uint fan)
    {
        try { return NvmlGetTargetFanSpeed(device, fan, out uint pct) == 0 ? pct : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static (uint Minimum, uint Maximum)? GetFanRange(IntPtr device)
    {
        try
        {
            int rc = NvmlGetMinMaxFanSpeed(device, out uint min, out uint max);
            return rc == 0 && min <= max && max <= 100 ? (min, max) : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static void SetFanPercent(IntPtr device, uint fan, uint percent)
    {
        int rc = NvmlSetFanSpeed(device, fan, Math.Clamp(percent, 0, 100));
        if (rc != 0) throw new CliError($"设置风扇 {fan} 转速失败 (错误码 {rc})。");
    }

    public static uint GetFanPolicy(IntPtr device, uint fan)
        => NvmlGetFanPolicy(device, fan, out uint pol) == 0 ? pol : 0;

    public static void SetFanPolicy(IntPtr device, uint fan, uint policy)
    {
        int rc = NvmlSetFanPolicy(device, fan, policy);
        if (rc != 0) throw new CliError($"设置风扇 {fan} 控制策略失败 (错误码 {rc})。");
    }

    public static uint GetTemperatureC(IntPtr device)
    {
        int rc = NvmlGetTemperature(device, 0, out uint t);
        if (rc != 0 || t > 150) throw new CliError($"读取显卡温度失败 (错误码 {rc}, 温度 {t})。");
        return t;
    }

    public static uint GetPowerMilliwatts(IntPtr device)
        => NvmlGetPower(device, out uint mw) == 0 ? mw : 0;

    public static uint GetClockMhz(IntPtr device, uint type)
        => NvmlGetClock(device, type, out uint mhz) == 0 ? mhz : 0;

    public static string GetDeviceName(IntPtr device)
    {
        var sb = new StringBuilder(96);
        return NvmlGetName(device, sb, 96) == 0 ? sb.ToString() : "未知设备";
    }
}
