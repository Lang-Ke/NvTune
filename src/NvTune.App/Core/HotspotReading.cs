using System.Globalization;

namespace NvTune;

/// <summary>A missing hotspot is a separate state, never a zero or a copy of the core sensor.</summary>
internal sealed record HotspotReading(double? Celsius, string Note, string Details)
{
    internal string ValueText => Celsius?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—";

    internal static HotspotReading Readout(double celsius) => double.IsFinite(celsius) && celsius > 0 && celsius <= 200
        ? new(celsius, "", "显卡驱动报告的 GPU 热点温度。风扇曲线仍按 GPU 温度调节。")
        : Invalid;
    internal static HotspotReading NotSupported(string reason) => new(null, "驱动未提供", reason);
    internal static HotspotReading Unavailable(string reason) => new(null, "读取失败", reason);
    internal static HotspotReading Waiting => new(null, "等待监测", "等待显卡初始化并读取热点温度。");
    internal static HotspotReading MonitoringUnavailable => new(null, "监测不可用", "当前无法连接显卡，热点温度暂不可读取。");
    private static HotspotReading Invalid => new(null, "读数无效", "驱动返回的热点温度不在可识别范围内，已隐藏该读数。");

    // Private NVAPI channel mapping used by LibreHardwareMonitor. Blackwell changes channel 1
    // to core temperature; unknown architectures must not inherit this older mapping.
    // https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs
    internal static bool HasKnownChannel(uint? architecture)
        => architecture is 0x130 or 0x140 or 0x150 or 0x160 or 0x170 or 0x190;

    internal static HotspotReading Read(uint? architecture, Func<(int Status, int RawTemperature)> readSensor)
    {
        if (!HasKnownChannel(architecture))
            return new(null, "暂不支持", architecture is >= 0x1B0
                ? "当前显卡架构的驱动温度通道无法可靠识别为热点；不会使用 GPU 温度代替热点温度。"
                : "暂时无法确认当前显卡架构的热点传感器，已隐藏读数；不会使用 GPU 温度代替。");
        try
        {
            var result = readSensor();
            return FromDriver(result.Status, result.RawTemperature);
        }
        catch (Exception ex)
        {
            return Unavailable("热点温度读取失败，下次刷新会重试。" + Environment.NewLine + ex.Message);
        }
    }

    internal static HotspotReading FromDriver(int status, int rawTemperature)
    {
        // NVIDIA's public NvAPI_Status definitions; private entry points may be absent or refused.
        if (status is -3 or -104 or -136)
            return NotSupported($"当前显卡或驱动未提供热点温度接口（NVAPI {status}）。");
        if (status == -121)
            return new(null, "未提供数据", "驱动尚未提供热点传感器数据，下次刷新会重试（NVAPI -121）。");
        if (status == -9)
            return new(null, "接口不兼容", "当前驱动不接受热点温度接口版本，无法读取热点温度（NVAPI -9）。");
        if (status != 0)
            return Unavailable($"驱动读取热点温度失败（NVAPI {status}），下次刷新会重试。");
        if (rawTemperature == 0)
            return NotSupported("驱动未返回有效的热点传感器数据；这不表示热点温度为 0°C。");
        return Readout(rawTemperature / 256.0);
    }
}
