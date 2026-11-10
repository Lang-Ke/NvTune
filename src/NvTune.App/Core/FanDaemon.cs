namespace SimpleNvidiaUndervolt;

/// <summary>Background fan worker. Reads the stored per-GPU fan settings through NVML and applies
/// them. A fixed percentage is written once and the process exits (nothing to keep alive, so it costs
/// zero resources after logon); a temperature curve is re-evaluated every few seconds, which is the
/// only reason this process stays resident. It also exits as soon as no GPU has a setting left, so
/// resetting to auto never leaves a stray process behind.</summary>
internal static class FanDaemon
{
    private const int PollMs = 5000;

    public static async Task Run(CancellationToken token)
    {
        using var single = new Mutex(initiallyOwned: true, DaemonControl.MutexName, out bool isFirst);
        if (!isFirst)
        {
            Diag.Log("风扇守护进程已在运行，本次启动退出。");
            return;
        }

        DaemonControl.WritePid();
        Diag.Log("风扇守护进程启动。");

        try
        {
            FanApi.Initialize();
        }
        catch (Exception ex)
        {
            Diag.Log("NVML 初始化失败，守护进程退出: " + ex.Message);
            return;
        }

        try
        {
            var devices = new Dictionary<int, (IntPtr Handle, uint Fans)>();
            int count = FanApi.GetDeviceCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    IntPtr dev = FanApi.GetDevice((uint)i);
                    uint fans = FanApi.GetFanCount(dev);
                    if (fans > 0) devices[i] = (dev, fans);
                }
                catch (Exception ex)
                {
                    Diag.Log($"NVML 打开 GPU {i} 失败: {ex.Message}");
                }
            }

            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                bool anySetting;
                bool anyCurve;
                try
                {
                    (anySetting, anyCurve) = ApplyOnce(devices);
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    Diag.Log($"风扇守护进程应用失败 ({failures}): {ex.Message}");
                    if (failures >= 6)
                    {
                        Diag.Log("风扇守护进程连续失败，退出。");
                        break;
                    }

                    await Task.Delay(PollMs, token).ConfigureAwait(false);
                    continue;
                }

                if (!anySetting)
                {
                    Diag.Log("没有风扇设置，守护进程退出。");
                    break;
                }

                if (!anyCurve)
                {
                    Diag.Log("仅固定转速设置，已应用一次，守护进程退出。");
                    break;
                }

                await Task.Delay(PollMs, token).ConfigureAwait(false);
            }
        }
        finally
        {
            FanApi.Shutdown();
            DaemonControl.ClearPid();
            Diag.Log("风扇守护进程已结束。");
        }
    }

    private static (bool AnySetting, bool AnyCurve) ApplyOnce(
        Dictionary<int, (IntPtr Handle, uint Fans)> devices)
    {
        bool anySetting = false;
        bool anyCurve = false;

        foreach (var (index, (handle, fans)) in devices)
        {
            var settings = FanState.Load(index);
            if (settings is null) continue;

            int target;
            if (settings.ModeValue == FanState.Mode.Fixed)
            {
                target = settings.FixedPercent ?? 50;
            }
            else if (settings.ModeValue == FanState.Mode.Curve && settings.Curve is { } curve)
            {
                target = curve.FanPercentAt((int)FanApi.GetTemperatureC(handle));
                anyCurve = true;
            }
            else
            {
                continue;
            }

            anySetting = true;
            for (uint f = 0; f < fans; f++)
            {
                if (FanApi.GetFanPolicy(handle, f) != FanApi.PolicyManual)
                    FanApi.SetFanPolicy(handle, f, FanApi.PolicyManual);
                FanApi.SetFanPercent(handle, f, (uint)target);
            }
        }

        return (anySetting, anyCurve);
    }
}