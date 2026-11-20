namespace NvTune;

/// <summary>Keep driver percentage, target duty and RPM distinct. A target is not a tachometer,
/// and missing data must not become a fabricated 0%/0 RPM.</summary>
internal sealed record FanTelemetry(uint Index, uint? ReportedPercent, uint? TargetPercent, uint? Rpm, uint? Policy)
{
    internal string Value => (Rpm ?? ReportedPercent)?.ToString() ?? "—";
    internal string Unit => Rpm is not null ? "RPM" : "%";
    internal string Summary => $"F{Index + 1} 读数 {Percent(ReportedPercent)} · 目标 {Percent(TargetPercent)}"
        + (Rpm is { } rpm ? $" · {rpm} RPM" : " · RPM 不可用")
        + (Policy == FanApi.PolicyAuto ? " · 自动" : Policy == FanApi.PolicyManual ? " · 手动" : " · 策略未知");
    internal static string Percent(uint? percent) => percent is { } p ? $"{p}%" : "—";

    internal static int EffectivePercent(int requested, (uint Minimum, uint Maximum)? range)
    {
        int bounded = Math.Clamp(requested, 0, 100);
        return range is { } r ? Math.Clamp(bounded, (int)r.Minimum, (int)r.Maximum) : bounded;
    }

    internal static void RequireAccepted(int effective, uint? driverTarget)
    {
        if (driverTarget is { } target && Math.Abs((long)target - effective) > 1)
            throw new CliError($"风扇请求 {effective}%，但驱动目标仍为 {target}%，无法确认设置已生效。");
    }
}
