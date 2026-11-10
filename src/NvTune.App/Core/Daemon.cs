using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// The one process the logon task runs. It applies everything that was saved, in a defined order:
/// first every GPU's undervolt, then the fan settings. The driver is often not ready in the first
/// seconds after logon, so both stages retry instead of giving up on the first failure.
///
/// It only stays resident when a fan temperature curve needs polling; a fixed fan speed or no fan
/// setting at all lets it exit, so the common case costs nothing after the apply.
/// </summary>
internal static class Daemon
{
    private const int FanPollMs = 4000;
    private const int DriverAttempts = 12;
    private static readonly TimeSpan DriverRetryDelay = TimeSpan.FromSeconds(5);
    private const int UndervoltAttempts = 5;
    private const int UndervoltRetryDelayMs = 4000;
    private const int IdleTickMs = 2000;
    private const int UndervoltCheckMs = 20000;

    /// <summary>The per-anchor offsets each GPU held right after its last successful apply. Comparing
    /// against these is how the watchdog notices that sleep/resume or a driver restart silently
    /// dropped the tuning.</summary>
    private static readonly Dictionary<int, int[]> AppliedDeltas = new();

    public static async Task<int> Run(CancellationToken token)
    {
        using var single = new Mutex(initiallyOwned: true, DaemonControl.MutexName, out bool isFirst);
        if (!isFirst)
        {
            Diag.Log("后台任务已在运行，本次启动退出。");
            return 0;
        }

        DaemonControl.WritePid();
        Diag.Log("后台任务启动。");

        try
        {
            if (!await WaitForNvApi(token).ConfigureAwait(false))
            {
                Diag.Log("NVIDIA 驱动在等待时间内未就绪，后台任务退出。");
                return 1;
            }

            await ApplyUndervolts(token).ConfigureAwait(false);
            await RunResidentLoop(token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Diag.Log("后台任务失败: " + ex);
            return 1;
        }
        finally
        {
            try { NvApi.Unload(); } catch { }
            try { FanApi.Shutdown(); } catch { }
            DaemonControl.ClearPid();
            Diag.Log("后台任务结束。");
        }
    }

    /// <summary>Waits for NVAPI to answer with a readable V/F curve. At logon the display driver can
    /// take a while to come up, and the first initialisation attempts fail outright.</summary>
    private static async Task<bool> WaitForNvApi(CancellationToken token)
    {
        for (int attempt = 1; attempt <= DriverAttempts; attempt++)
        {
            try
            {
                NvApi.Initialize();
                if (NvApi.EnumeratePhysicalGpus().Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"等待 NVIDIA 驱动 ({attempt}/{DriverAttempts}): {ex.Message}");
            }

            if (attempt == DriverAttempts)
            {
                break;
            }

            try { await Task.Delay(DriverRetryDelay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    /// <summary>Re-applies every stored tuning document. Each GPU is independent: one card failing
    /// (a document from a card that is gone, an unreadable curve) must not stop the others.</summary>
    private static async Task ApplyUndervolts(CancellationToken token)
    {
        IReadOnlyList<int> stored = PersistedTuning.StoredGpuIndexes();
        if (stored.Count == 0)
        {
            Diag.Log("没有已保存的降压设置。");
            return;
        }

        IntPtr[] gpus;
        try
        {
            gpus = NvApi.EnumeratePhysicalGpus();
        }
        catch (Exception ex)
        {
            Diag.Log("枚举显卡失败: " + ex.Message);
            return;
        }

        foreach (int index in stored)
        {
            if (index >= gpus.Length)
            {
                Diag.Log($"已保存的 GPU {index} 不存在，跳过。");
                continue;
            }

            for (int attempt = 1; attempt <= UndervoltAttempts; attempt++)
            {
                try
                {
                    ApplyOne(gpus, index);
                    break;
                }
                catch (Exception ex)
                {
                    Diag.Log($"GPU {index} 降压重新应用失败 ({attempt}/{UndervoltAttempts}): {ex.Message}");

                    // At logon the card idles, and the driver can briefly report a collapsed
                    // frequency column while it settles into its lowest power state. That is the
                    // one failure worth waiting out: a document from another card, or a table
                    // this GPU cannot hold, fails identically however long we wait.
                    if (attempt == UndervoltAttempts || !IsTransientRead(ex))
                    {
                        break;
                    }

                    await Delay(UndervoltRetryDelayMs, token).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool IsTransientRead(Exception ex)
        => ex.Message.Contains(GpuTuning.TransientReadMarker, StringComparison.Ordinal);

    private static void ApplyOne(IntPtr[] gpus, int index)
    {
        IntPtr gpu = gpus[index];
        TuningDoc doc = PersistedTuning.Load(index);
        TuningDocuments.RequireMatchesGpu(doc.GpuName, doc.GpuPciIds, gpu,
            $"GPU {index} 的已保存降压");

        // The anchor voltages are power-state independent, so the live curve is usable at idle;
        // only a genuinely unrecognised table (a different layout) is refused.
        IReadOnlyList<(int Mv, int Mhz)> live = NvApi.GetVfCurve(gpu);
        if (!GpuTuning.CurveVoltsPlausible(live))
        {
            Diag.Log($"GPU {index} 的 V/F 曲线无法识别，跳过降压。");
            return;
        }

        var reference = ReferenceCurve.Match(new SelectedGpu(gpu, index, gpus.Length));
        GpuTuning.ApplyExact(gpu, doc, reference.Curve, live, $"GPU {index} 的已保存降压");
        AppliedDeltas[index] = NvApi.GetCurveFreqDeltasKhz(gpu, NvApi.MaxCurveAnchors);
        Diag.Log($"GPU {index} 降压已重新应用。");
    }

    /// <summary>
    /// Re-applies any stored undervolt the card is no longer holding. Sleep/resume and a driver
    /// restart both reset the V/F offsets, which is exactly the "it didn't survive" complaint a
    /// logon-only apply produces. Returns whether any stored undervolt is still worth watching.
    /// </summary>
    private static bool WatchUndervolts()
    {
        IReadOnlyList<int> stored = PersistedTuning.StoredGpuIndexes();
        if (stored.Count == 0)
        {
            AppliedDeltas.Clear();
            return false;
        }

        foreach (int gone in AppliedDeltas.Keys.Where(i => !stored.Contains(i)).ToArray())
        {
            AppliedDeltas.Remove(gone);
        }

        IntPtr[] gpus;
        try
        {
            gpus = NvApi.EnumeratePhysicalGpus();
        }
        catch (Exception ex)
        {
            Diag.Log("看护降压时枚举显卡失败: " + ex.Message);
            return true;
        }

        foreach (int index in stored)
        {
            if (index >= gpus.Length)
            {
                continue;
            }

            try
            {
                int[] live = NvApi.GetCurveFreqDeltasKhz(gpus[index], NvApi.MaxCurveAnchors);
                if (AppliedDeltas.TryGetValue(index, out int[]? expected) && SameDeltas(expected, live))
                {
                    continue;
                }

                Diag.Log($"GPU {index} 的降压已不在显卡上（睡眠唤醒或驱动重置），重新应用。");
                ApplyOne(gpus, index);
            }
            catch (Exception ex)
            {
                Diag.Log($"GPU {index} 看护降压失败: {ex.Message}");
            }
        }

        return true;
    }

    private static bool SameDeltas(int[] a, int[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The resident stage. A temperature curve needs a poll (the fan speed follows the sensor), and
    /// an undervolt needs a watchdog: sleep/resume and a driver restart both drop the applied curve,
    /// which is the other half of "the settings didn't survive". Each is polled at its own rate, so
    /// a fixed fan speed alone still lets the process exit — the common case costs nothing.
    /// </summary>
    private static async Task RunResidentLoop(CancellationToken token)
    {
        bool hasUndervolt = PersistedTuning.StoredGpuIndexes().Count > 0;
        bool hasFan = FanState.StoredGpuIndexes().Count > 0;
        if (!hasUndervolt && !hasFan)
        {
            Diag.Log("没有需要常驻的设置，后台任务退出。");
            return;
        }

        bool fanReady = false;
        if (hasFan)
        {
            try
            {
                FanRunner.Initialize(NvApi.EnumeratePhysicalGpus().Length);
                fanReady = true;
            }
            catch (Exception ex)
            {
                Diag.Log("NVML 初始化失败，风扇设置未应用: " + ex.Message);
            }
        }

        int failures = 0;
        var undervoltClock = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            bool anyCurve = false;
            if (fanReady)
            {
                try
                {
                    (bool anySetting, bool curve) = FanRunner.ApplyOnce();
                    anyCurve = curve;
                    failures = 0;
                    if (!anySetting)
                    {
                        Diag.Log("风扇设置已清空，停止风扇调节。");
                        fanReady = false;
                    }
                    else if (!curve)
                    {
                        // A fixed percentage holds on its own; only a temperature curve needs
                        // someone watching the sensor.
                        Diag.Log("仅固定转速，已应用，停止风扇调节。");
                        fanReady = false;
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    Diag.Log($"风扇设置应用失败 ({failures}): {ex.Message}");
                    if (failures >= 6)
                    {
                        Diag.Log("风扇设置连续失败，停止风扇调节。");
                        fanReady = false;
                        failures = 0;
                    }
                }
            }

            if (hasUndervolt && undervoltClock.ElapsedMilliseconds >= UndervoltCheckMs)
            {
                undervoltClock.Restart();
                hasUndervolt = WatchUndervolts();
            }

            // A fixed speed has nothing to follow, so with no curve and no undervolt left to
            // watch there is no reason to stay resident.
            if (!fanReady && !hasUndervolt)
            {
                Diag.Log("已无可常驻的设置，后台任务退出。");
                return;
            }

            await Delay(anyCurve ? FanPollMs : IdleTickMs, token).ConfigureAwait(false);
        }
    }

    private static async Task Delay(int ms, CancellationToken token)
    {
        try { await Task.Delay(ms, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
