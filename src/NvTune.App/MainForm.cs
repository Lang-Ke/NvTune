using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

internal sealed partial class MainForm : Form
{
    private static int D(int px) => Theme.Scaled(px);

    private readonly DarkCombo _gpuSelector = Theme.Combo(340);
    private readonly Label _taskBadge = new()
    {
        // A fixed 36px box matches the theme button beside it, so both share one centre line
        // instead of differing by the rounding of their own text heights.
        AutoSize = false,
        Height = Theme.Scaled(36),
        Width = Theme.Scaled(132),
        Padding = new Padding(0),
        BackColor = Theme.AccentSoft,
        ForeColor = Theme.Accent,
        Font = Theme.SmallBoldFont(),
        TextAlign = ContentAlignment.MiddleCenter,
        Anchor = AnchorStyles.None,
    };
    private readonly DarkNumeric _mvInput = Theme.Numeric(400, 1400, 950);
    private readonly DarkNumeric _mhzInput = Theme.Numeric(200, 4200, 1800);
    private readonly DarkNumeric _memInput = Theme.Numeric(-2000, 2000, 0);

    private readonly DarkCombo _fanMode = Theme.Combo(140);
    private readonly DarkNumeric _fanFixed = Theme.Numeric(0, 100, 50, 78);
    private readonly FanCurveEditor _curveEditor = new() { Dock = DockStyle.Fill };
    private readonly Label _daemonLabel = new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.SmallFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(D(2), 0, 0, 0),
    };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Bottom,
        Height = Theme.Scaled(40),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(D(20), 0, 0, 0),
        ForeColor = Theme.Text,
        BackColor = Theme.Surface,
    };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _fanTimer = new() { Interval = 4000 };
    private readonly FanController _fanController = FanRunner.CreateController();
    private readonly Stopwatch _undervoltClock = Stopwatch.StartNew();
    private FileStream? _controlLease;
    private FileStream? _guiLease;

    private IntPtr[] _gpus = Array.Empty<IntPtr>();
    private readonly Dictionary<int, string> _gpuUuids = new();
    private IReadOnlyList<(int Mv, int Mhz)> _anchors = Array.Empty<(int, int)>();
    private bool _loading;
    private readonly bool _preview;
    private string? _fanSummary;

    /// <summary>Undervolt edits are tracked per GPU, with the edited values kept per card too:
    /// switching cards must neither carry a pending edit onto the other card's saved file nor
    /// silently discard what the user typed before switching.</summary>
    private readonly HashSet<int> _dirtyUndervoltGpus = new();
    private readonly Dictionary<int, UndervoltDraft> _undervoltDrafts = new();
    private readonly HashSet<int> _tempUndervoltGpus = new();
    private readonly HashSet<int> _tempUndervoltCleared = new();
    private readonly Dictionary<int, FanState.FanSettings> _tempFanSettings = new();
    private readonly HashSet<int> _tempFanCleared = new();
    private sealed record UndervoltDraft(int Mv, int Mhz, int MemMhz);

    public MainForm(string[] args, bool preview = false)
    {
        _preview = preview;
        SuspendLayout();
        Text = "NvTune 1.0.10 · NVIDIA 降压与风扇";
        Font = Theme.BodyFont();
        BackColor = Theme.Canvas;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        ResizeRedraw = true;
        ClientSize = new Size(D(960), D(760));
        MinimumSize = new Size(D(640), D(480));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        BuildLayout();
        _fanMode.Items.AddRange(new object[] { "自动", "固定转速", "温度曲线" });
        _pollTimer.Tick += (_, _) => UpdateLiveStatus();
        _fanTimer.Tick += (_, _) => ApplyTemporaryFanCurve();
        _curveEditor.PointsChanged += (_, _) => MarkFanDirty();
        _fanMode.SelectedIndexChanged += (_, _) => OnFanModeChanged();
        _mvInput.ValueChanged += (_, _) => OnVoltageEdited();
        _mhzInput.ValueChanged += (_, _) => MarkUndervoltDirty();
        _memInput.ValueChanged += (_, _) => MarkUndervoltDirty();
        if (!preview)
        {
            Load += (_, _) =>
            {
                FitToWorkingArea();
                SetStatus("正在初始化显卡接口…", Theme.Muted);
                _ = InitializeGpusAsync();
            };
        }
        Shown += (_, _) => ForcePaint();
        ResumeLayout(true);
        if (preview) PopulatePreview();
    }
    private async Task InitializeGpusAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                NvApi.Initialize();
                FanRunner.Initialize();
                _gpus = NvApi.EnumeratePhysicalGpus();
            });
            if (_gpus.Length == 0)
            {
                SetStatus("未检测到 NVIDIA 显卡。", Theme.Danger);
                return;
            }

            if (IsDisposed || Disposing) return;
            _guiLease = HardwareControlLease.TryAcquire(Path.Combine(Persistence.DataDir(), "gui-instance.lock"))
                ?? throw new CliError("已有 NvTune 窗口正在运行。");
            DaemonControl.Stop();
            _controlLease = HardwareControlLease.TryAcquire()
                ?? throw new CliError("另一个 NvTune 窗口或后台正在控制显卡，请关闭后重试。");
            DaemonControl.Start(recoverFans: true);
            _fanTimer.Start();

            _loading = true;
            for (int i = 0; i < _gpus.Length; i++)
            {
                _gpuSelector.Items.Add(NvApi.SafeFullName(_gpus[i]));
                try { _gpuUuids[i] = FanRunner.Uuid(i); }
                catch (Exception ex) { Diag.Log($"GPU {i} 无法绑定当前会话: {ex.Message}"); }
            }
            _gpuSelector.SelectedIndex = 0;
            _loading = false;
            await Task.Run(() => _anchors = NvApi.GetVfCurve(SelectedGpuHandle));
            OnGpuChanged();
            _pollTimer.Start();
            SetStatus("就绪。「保存并开机应用」重启后仍然生效；两处「本次应用」仅在本次运行期间生效。", Theme.Success);
            ApplyTemporaryFanCurve();
            UpdateTaskBadge();
            Diag.Log($"界面初始化完成，检测到 {_gpus.Length} 张 NVIDIA 显卡。");
        }
        catch (Exception ex)
        {
            _fanTimer.Stop();
            _controlLease?.Dispose();
            _controlLease = null;
            _guiLease?.Dispose();
            _guiLease = null;
            _gpus = Array.Empty<IntPtr>();
            UpdateHotspotMetric(HotspotReading.MonitoringUnavailable);
            SetStatus("初始化失败: " + ex.Message, Theme.Danger);
            Diag.Log("初始化失败: " + ex);
        }

        _ = Task.Run(Scheduler.RemoveLegacyTasks);
    }

    private void ForcePaint()
    {
        foreach (Control control in Controls) control.Refresh();
        Refresh();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); // WinForms scales the tree exactly once, including point fonts.
        FitToWorkingArea();
        UpdateContentWidth();
        Invalidate(true);
    }
    private int SelectedIndex => Math.Max(0, _gpuSelector.SelectedIndex);
    private IntPtr SelectedGpuHandle => _gpus.Length > SelectedIndex ? _gpus[SelectedIndex] : IntPtr.Zero;
    private bool UndervoltDirty => _dirtyUndervoltGpus.Contains(SelectedIndex);

    private void RequireSelectedGpuIdentity()
    {
        string? expected = _gpuUuids.GetValueOrDefault(SelectedIndex);
        GpuBinding.Require(expected, FanRunner.Uuid(SelectedIndex));
        GpuBinding.Require(expected, FanRunner.Uuid(SelectedGpuHandle));
    }

    private void OnGpuChanged()
    {
        if (_loading || SelectedGpuHandle == IntPtr.Zero) return;
        try
        {
            _anchors = NvApi.GetVfCurve(SelectedGpuHandle);
            if (!GpuTuning.CurveVoltsPlausible(_anchors) || _anchors.Count < 2)
            {
                SetStatus("此显卡的 V/F 曲线无法识别，降压功能不可用。", Theme.Warning);
            }
            else
            {
                ConfigureVoltageRange();
            }
        }
        catch (Exception ex) { SetStatus("读取显卡曲线失败: " + ex.Message, Theme.Danger); }

        LoadFanStateForGpu();
        UpdateLiveStatus();
    }

    private void ConfigureVoltageRange()
    {
        int lowest = _anchors[0].Mv, highest = _anchors[^1].Mv;
        _undervoltDrafts.TryGetValue(SelectedIndex, out UndervoltDraft? draft);
        bool hasSavedTuning = PersistedTuning.StoredGpuIndexes().Contains(SelectedIndex);
        if (draft is null && hasSavedTuning && UndervoltUiState.Load(SelectedIndex) is { } saved)
        {
            draft = new UndervoltDraft(saved.Mv, saved.Mhz, saved.MemMhz);
        }

        _loading = true;
        _mvInput.Minimum = lowest;
        _mvInput.Maximum = highest;
        int maxClock = _anchors.Max(a => a.Mhz);
        _mhzInput.Minimum = Math.Max(200, _anchors.Min(a => a.Mhz) - 200);
        _mhzInput.Maximum = Math.Min(4200, maxClock + 500);
        if (draft is not null)
        {
            _mvInput.Value = Math.Clamp(SnapToAnchor(draft.Mv), lowest, highest);
            _mhzInput.Value = Math.Clamp(draft.Mhz, _mhzInput.Minimum, _mhzInput.Maximum);
            _memInput.Value = Math.Clamp(draft.MemMhz, _memInput.Minimum, _memInput.Maximum);
        }
        else
        {
            _mvInput.Value = SnapToAnchor(Math.Max(lowest, highest - 180));
            _memInput.Value = 0;
        }

        _loading = false;
        if (draft is null)
        {
            SyncClockToVoltage();
        }

        string savedText = hasSavedTuning ? " · 已载入上次保存的降压参数" : string.Empty;
        SetStatus($"可调电压 {lowest} - {highest} mV，已按本卡曲线自动适配{savedText}。", Theme.Muted);
    }

    private int SnapToAnchor(int mv)
    {
        int best = _anchors[0].Mv;
        foreach (var (m, _) in _anchors) if (Math.Abs(m - mv) < Math.Abs(best - mv)) best = m;
        return best;
    }

    private void SyncClockToVoltage()
    {
        if (_anchors.Count < 2) return;
        int snapped = (int)(Math.Round(GpuTuning.FreqAtVoltage(_anchors, (double)_mvInput.Value) / 15.0) * 15);
        bool loading = _loading;
        _loading = true;
        _mhzInput.Value = Math.Clamp(snapped, _mhzInput.Minimum, _mhzInput.Maximum);
        _loading = loading;
    }

    /// <summary>Voltage is picked on the card's own anchors, and the matching clock follows it as
    /// the starting point the user then lowers.</summary>
    private void OnVoltageEdited()
    {
        if (_loading) return;
        if (_anchors.Count >= 2)
        {
            // A spinner step walks to the neighbouring anchor. Snapping a stepped value back to
            // the nearest anchor would undo every click, so the arrows would look dead.
            int target = _mvInput.Stepping
                ? StepAnchor((int)_mvInput.Value, _mvInput.StepDirection)
                : SnapToAnchor((int)_mvInput.Value);
            int snapped = target;
            if (snapped != (int)_mvInput.Value)
            {
                _loading = true;
                _mvInput.Value = snapped;
                _loading = false;
            }

            SyncClockToVoltage();
        }

        MarkUndervoltDirty();
    }

    /// <summary>The next anchor above or below the given voltage, so the arrows move through the
    /// card's real V/F points instead of landing on values the curve cannot use.</summary>
    private int StepAnchor(int mv, int direction)
    {
        var ordered = _anchors.OrderBy(a => a.Mv).ToArray();
        if (direction > 0)
        {
            foreach (var anchor in ordered) if (anchor.Mv > mv) return anchor.Mv;
            return ordered[^1].Mv;
        }

        for (int i = ordered.Length - 1; i >= 0; i--) if (ordered[i].Mv < mv) return ordered[i].Mv;
        return ordered[0].Mv;
    }

    private void MarkUndervoltDirty()
    {
        if (_loading) return;
        _dirtyUndervoltGpus.Add(SelectedIndex);
        _undervoltDrafts[SelectedIndex] = new UndervoltDraft(
            (int)_mvInput.Value, (int)_mhzInput.Value, (int)_memInput.Value);
        SetStatus("降压参数已修改。点「本次应用」仅本次运行生效，点「保存并开机应用」重启后仍生效。", Theme.Warning);
    }

    private void ApplyUndervolt()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero || _controlLease is null) return;
        try
        {
            RequireSelectedGpuIdentity();
            _tempUndervoltGpus.Add(SelectedIndex);
            _tempUndervoltCleared.Remove(SelectedIndex);
            var stock = GpuTuning.ResetAndReadStock(gpu);
            int mv = (int)_mvInput.Value, mhz = (int)_mhzInput.Value, memDelta = (int)_memInput.Value * 1000;
            var plan = GpuTuning.BuildCurvePlan(stock, mv, mhz, TuneRequest.DefaultCapPoints);
            foreach (string line in GpuTuning.Apply(gpu, plan, stock, mhz, memDelta)) Diag.Log(line);
            _dirtyUndervoltGpus.Remove(SelectedIndex);
            _tempUndervoltCleared.Remove(SelectedIndex);
            _tempUndervoltGpus.Add(SelectedIndex);
            SetStatus($"降压已临时应用（约 {plan.SettleMv} mV / {plan.SettleMhz} MHz），关闭软件后恢复已保存配置。", Theme.Success);

            UpdateTaskBadge();
            UpdateDaemonLabel();
        }
        catch (Exception ex)
        {
            SetStatus("应用失败: " + ex.Message, Theme.Danger);
            Diag.Log("应用降压失败: " + ex);
            MessageBox.Show(this, ex.Message, "本次应用失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateLiveStatus();
    }

    /// <summary>Verify the selected GPU's settings before persisting them. The GUI owns the
    /// controller while open; closing the window hands the saved settings back to the daemon.</summary>
    private void SaveAllSettings()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero || _controlLease is null) return;
        try
        {
            RequireSelectedGpuIdentity();
            DaemonControl.Stop(); // Stop the standby copy before updating installed program files.
            Persistence.EnsureInstalled();
            if (_tempUndervoltCleared.Contains(SelectedIndex))
            {
                PersistedTuning.Remove(SelectedIndex);
                UndervoltUiState.Delete(SelectedIndex);
                _dirtyUndervoltGpus.Remove(SelectedIndex);
                _tempUndervoltCleared.Remove(SelectedIndex);
            }
            else if (UndervoltDirty || _tempUndervoltGpus.Contains(SelectedIndex)
                || (PersistedTuning.StoredGpuIndexes().Contains(SelectedIndex)
                    && PersistedTuning.Load(SelectedIndex).GpuUuid is null))
            {
                // Track before the first reset so even a partial/failed save can restore on close.
                _tempUndervoltGpus.Add(SelectedIndex);
                string uuid = FanRunner.Uuid(SelectedIndex);
                var stock = GpuTuning.ResetAndReadStock(gpu);
                int mv = (int)_mvInput.Value, mhz = (int)_mhzInput.Value, memDelta = (int)_memInput.Value * 1000;
                var plan = GpuTuning.BuildCurvePlan(stock, mv, mhz, TuneRequest.DefaultCapPoints);
                var diagnostics = GpuTuning.Apply(gpu, plan, stock, mhz, memDelta);
                foreach (string line in diagnostics) Diag.Log(line);
                var doc = TuningDocuments.MakeTuningDoc(GpuIdentity.Read(gpu), stock,
                    new AppliedTuning(plan.DeltasKhz, memDelta));
                doc.GpuUuid = uuid;
                PersistedTuning.Save(doc, SelectedIndex);
                UndervoltUiState.Save(new UndervoltUiState.Values(mv, mhz, (int)_memInput.Value), SelectedIndex);
                Daemon.RememberApplied(SelectedIndex, gpu);
                _dirtyUndervoltGpus.Remove(SelectedIndex);
                _tempUndervoltGpus.Remove(SelectedIndex);
            }

            int mode = _fanMode.SelectedIndex;
            var settings = new FanState.FanSettings(mode,
                mode == 1 ? (int)_fanFixed.Value : null,
                mode == 2 ? _curveEditor.GetPoints().Select(p => new[] { p.TempC, p.Percent }).ToArray() : null,
                FanRunner.Uuid(SelectedIndex));
            _tempFanSettings[SelectedIndex] = settings;
            _tempFanCleared.Remove(SelectedIndex);
            _fanController.Apply(SelectedIndex, settings);
            if (mode == 0) FanState.Delete(SelectedIndex);
            else FanState.Save(settings, SelectedIndex);
            _tempFanSettings.Remove(SelectedIndex);
            SyncSchedulerToSavedSettings();
            ApplyTemporaryFanCurve();
            UpdateTaskBadge();
            UpdateDaemonLabel();
            string pending = _dirtyUndervoltGpus.Count > 0 ? " 其他显卡仍有未保存的降压改动。" : string.Empty;
            SetStatus("配置已保存并应用，下次登录时自动应用。" + pending, Theme.Success);
        }
        catch (Exception ex)
        {
            SetStatus("写入配置失败: " + ex.Message, Theme.Danger);
            Diag.Log("写入配置失败: " + ex);
            MessageBox.Show(this, ex.Message, "写入配置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try { DaemonControl.Start(recoverFans: true); }
            catch (Exception ex) { Diag.Log("启动待命后台失败: " + ex.Message); }
        }
        UpdateLiveStatus();
    }
    private void ResetUndervolt()
    {
        if (_controlLease is null || SelectedGpuHandle == IntPtr.Zero) return;
        try
        {
            RequireSelectedGpuIdentity();
            _tempUndervoltGpus.Add(SelectedIndex);
            GpuTuning.Clear(SelectedGpuHandle);
            _dirtyUndervoltGpus.Remove(SelectedIndex);
            _undervoltDrafts.Remove(SelectedIndex);
            _tempUndervoltGpus.Remove(SelectedIndex);
            _tempUndervoltCleared.Add(SelectedIndex);
            SetStatus("已临时恢复默认电压，关闭软件后恢复已保存配置。", Theme.Success);
            UpdateTaskBadge();
            UpdateDaemonLabel();
        }
        catch (Exception ex) { SetStatus("恢复失败: " + ex.Message, Theme.Danger); }
    }

    private void LoadFanStateForGpu()
    {
        FanState.FanSettings? settings;
        try { settings = FanState.Load(SelectedIndex); }
        catch (Exception ex) { settings = null; SetStatus(ex.Message, Theme.Warning); }
        if (_tempFanSettings.TryGetValue(SelectedIndex, out var temporary)) settings = temporary;
        if (_tempFanCleared.Contains(SelectedIndex)) settings = null;
        _loading = true;
        if (settings is null)
        {
            _fanMode.SelectedIndex = 0;
            _curveEditor.SetPoints(FanCurve.Default.Points);
        }
        else
        {
            _fanMode.SelectedIndex = Math.Clamp((int)settings.ModeValue, 0, 2);
            if (settings.FixedPercent is { } fp) _fanFixed.Value = Math.Clamp(fp, 0, 100);
            _curveEditor.SetPoints(settings.Curve?.Points ?? FanCurve.Default.Points);
        }

        _loading = false;
        OnFanModeChanged();
    }

    private void OnFanModeChanged()
    {
        _fanFixed.Enabled = _fanMode.SelectedIndex == 1;
        _curveEditor.Enabled = _fanMode.SelectedIndex == 2;
        UpdateDaemonLabel();
    }

    private void MarkFanDirty()
    {
        if (!_loading && _fanMode.SelectedIndex == 2)
        {
            SetStatus("曲线已修改。点「本次应用」仅本次运行生效，点「保存并开机应用」重启后仍生效。", Theme.Warning);
        }
    }

    private void ApplyFanSettings()
    {
        if (_controlLease is null) return;
        int mode = _fanMode.SelectedIndex;
        if (mode == 0) { RestoreFanAuto(); return; }

        try
        {
            RequireSelectedGpuIdentity();
            IntPtr dev = FanRunner.Device(SelectedIndex);
            if (FanRunner.FanCount(dev) == 0) { SetStatus("此显卡没有可通过软件控制的风扇。", Theme.Warning); return; }

            var settings = mode == 1
                ? new FanState.FanSettings(mode, (int)_fanFixed.Value, null)
                : new FanState.FanSettings(mode, null, _curveEditor.GetPoints().Select(p => new[] { p.TempC, p.Percent }).ToArray());
            settings = settings with { GpuUuid = FanRunner.Uuid(SelectedIndex) };
            _tempFanCleared.Remove(SelectedIndex);
            _tempFanSettings[SelectedIndex] = settings;
            _fanController.Apply(SelectedIndex, settings);
            SetStatus(mode == 2 ? "风扇曲线已临时应用，请核对驱动目标与读数。" : $"已提交风扇设定 {settings.FixedPercent}%，请核对驱动目标与读数。", Theme.Success);
            UpdateLiveStatus();
            UpdateTaskBadge();
            UpdateDaemonLabel();
        }
        catch (Exception ex)
        {
            SetStatus("风扇设置失败: " + ex.Message, Theme.Danger);
            Diag.Log("应用风扇设置失败: " + ex);
            MessageBox.Show(this, ex.Message, "风扇设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RestoreFanAuto()
    {
        if (_controlLease is null) return;
        try
        {
            RequireSelectedGpuIdentity();
            _tempFanSettings.Remove(SelectedIndex);
            _tempFanCleared.Add(SelectedIndex);
            _fanController.Apply(SelectedIndex, null);
            _loading = true;
            _fanMode.SelectedIndex = 0;
            _loading = false;
            OnFanModeChanged();
            SetStatus("已临时恢复默认风扇控制，关闭软件后恢复已保存配置。", Theme.Success);
            UpdateTaskBadge();
            UpdateDaemonLabel();
        }
        catch (Exception ex) { SetStatus("恢复默认风扇失败: " + ex.Message, Theme.Danger); }
    }

    private void ApplyTemporaryFanCurve()
    {
        if (_controlLease is null) return;
        try
        {
            FanRunner.Initialize();
            int[] indexes = FanState.StoredGpuIndexes().Concat(_tempFanSettings.Keys).Concat(_tempFanCleared).Distinct().ToArray();
            bool ok = _fanController.Tick(indexes, index =>
            {
                if (_tempFanCleared.Contains(index)) return null;
                if (_tempFanSettings.TryGetValue(index, out var temporary))
                {
                    GpuBinding.Require(temporary.GpuUuid, FanRunner.Uuid(index));
                    return temporary;
                }
                return FanRunner.LoadSaved(index);
            });
            if (!ok) SetStatus("部分显卡风扇控制失败，已尝试恢复自动控制，将继续重试。详情见日志。", Theme.Warning);
            if (_undervoltClock.ElapsedMilliseconds >= 20000)
            {
                _undervoltClock.Restart();
                Daemon.WatchUndervolts(_tempUndervoltGpus.Concat(_tempUndervoltCleared).ToHashSet());
            }
        }
        catch (Exception ex)
        {
            _fanController.Tick(FanRunner.MappedIndexes(), _ => null);
            SetStatus("显卡接口暂不可用，将重试: " + ex.Message, Theme.Warning);
            Diag.Log("界面控制循环失败: " + ex.Message);
        }
    }
    private void ClearEverything()
    {
        if (_controlLease is null) return;
        if (MessageBox.Show(this, "将清除所有降压与风扇设置，恢复显卡默认状态，并删除开机任务。\n\n继续？",
                "全部恢复出厂", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _fanTimer.Stop();
        _tempFanSettings.Clear();
        _tempFanCleared.Clear();
        _tempUndervoltGpus.Clear();
        _tempUndervoltCleared.Clear();
        var errors = new List<string>();
        void Attempt(Action action) { try { action(); } catch (Exception ex) { errors.Add(ex.Message); } }
        foreach (IntPtr gpu in _gpus)
            if (gpu != IntPtr.Zero) Attempt(() =>
            {
                int index = Array.IndexOf(_gpus, gpu);
                GpuBinding.Require(_gpuUuids.GetValueOrDefault(index), FanRunner.Uuid(gpu));
                GpuTuning.Clear(gpu);
            });
        if (!_fanController.Tick(FanRunner.MappedIndexes(), _ => null)) errors.Add("部分显卡的自动风扇控制未能恢复。");
        foreach (int index in FanState.StoredGpuIndexes()) Attempt(() => FanState.Delete(index));
        foreach (int index in PersistedTuning.StoredGpuIndexes())
        {
            Attempt(() => PersistedTuning.Remove(index));
            Attempt(() => UndervoltUiState.Delete(index));
        }
        Attempt(Scheduler.Remove);
        Attempt(Scheduler.RemoveLegacyTasks);
        _dirtyUndervoltGpus.Clear();
        _undervoltDrafts.Clear();
        LoadFanStateForGpu();
        if (_anchors.Count >= 2) ConfigureVoltageRange();
        _fanTimer.Start();
        if (errors.Count > 0)
        {
            Diag.Log("恢复出厂部分失败: " + string.Join("; ", errors));
            SetStatus("部分设置未能恢复，请检查日志并重试。", Theme.Warning);
        }
        else SetStatus("已恢复出厂，保存配置与开机任务已清除。", Theme.Success);
        UpdateTaskBadge();
        UpdateDaemonLabel();
    }
    private void SyncSchedulerToSavedSettings()
    {
        bool hasSettings = PersistedTuning.StoredGpuIndexes().Count > 0 || FanState.StoredGpuIndexes().Count > 0;
        if (hasSettings) Scheduler.Register(); else Scheduler.Remove();
    }

    private static bool HasSavedUndervolt() => PersistedTuning.StoredGpuIndexes().Count > 0;

    private static bool HasSavedFanCurve()
        => FanState.StoredGpuIndexes().Any(i =>
        {
            try { return FanState.Load(i)?.ModeValue == FanState.Mode.Curve; }
            catch { return true; }
        });

    private void UpdateLiveStatus()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero)
        {
            UpdateHotspotMetric(HotspotReading.MonitoringUnavailable);
            return;
        }
        UpdateHotspotMetric(NvApi.GetHotspotTemperature(gpu));
        if (!FanRunner.TryDevice(SelectedIndex, out IntPtr dev)) return;
        try
        {
            uint temp = FanRunner.TemperatureC(dev);
            var fans = FanRunner.ReadFanTelemetry(dev);
            uint power = FanRunner.PowerMilliwatts(dev);
            uint coreMhz = FanRunner.CoreMhz(dev);
            uint mv = NvApi.GetCoreVoltageUv(gpu) / 1000;
            _curveEditor.CurrentTemp = (int)temp;
            UpdateMetrics(temp, 0, power / 1000, coreMhz, mv);
            var first = fans.FirstOrDefault();
            _fanMetric.Value = first?.Value ?? "—";
            _fanMetric.Unit = first?.Unit ?? "%";
            int? request = _fanController.RequestedPercent(SelectedIndex);
            var range = FanApi.GetFanRange(dev);
            string rangeText = range is { } r ? $"手动范围 {r.Minimum}–{r.Maximum}%" : "手动范围未提供";
            string requestText = request is { } requested ? $"设定 {requested}% · " : "";
            _fanSummary = requestText + (first?.Summary ?? "风扇读数不可用");
            string details = requestText + rangeText + Environment.NewLine + string.Join(Environment.NewLine, fans.Select(f => f.Summary));
            _tips.SetToolTip(_fanMetric, details);
            _tips.SetToolTip(_daemonLabel, details);
            _tips.SetToolTip(_fanFixed, rangeText + "。低于范围的设定由驱动允许的最低值执行；0% 不保证停转。");
            UpdateDaemonLabel();
        }
        catch
        {
            _fanSummary = null;
            _curveEditor.CurrentTemp = -1;
            foreach (var metric in _metrics)
                if (metric != _hotspotMetric) metric.Value = "—";
            SetStatus("监测暂不可用，正在等待驱动恢复。", Theme.Warning);
        }
    }

    private void UpdateTaskBadge()
    {
        if (!IsHandleCreated) return;
        bool registered = Scheduler.Exists();
        bool hasSettings = PersistedTuning.StoredGpuIndexes().Count > 0 || FanState.StoredGpuIndexes().Count > 0;
        if (_tempUndervoltGpus.Contains(SelectedIndex) || _tempFanSettings.ContainsKey(SelectedIndex)
            || _tempUndervoltCleared.Contains(SelectedIndex) || _tempFanCleared.Contains(SelectedIndex))
        {
            _taskBadge.Text = "本次运行临时生效";
            SetBadgeColors(Theme.Accent);
        }
        else if (registered && hasSettings)
        {
            _taskBadge.Text = "✓  登录自动应用";
            SetBadgeColors(Theme.Success);
        }
        else if (hasSettings)
        {
            _taskBadge.Text = "!  任务未注册";
            SetBadgeColors(Theme.Warning);
        }
        else
        {
            _taskBadge.Text = "未保存配置";
            // A neutral slate rather than a pale tint: this is the state a fresh install opens
            // in, so it has to read at a glance without looking like an error.
            SetBadgeColors(Theme.Mix(Theme.Muted, Theme.Text, 0.55f));
        }
    }

    /// <summary>The soft tints this badge used to carry sat within a few points of the window
    /// background, so the state was effectively invisible. A solid fill with white text keeps
    /// every state legible against the canvas.</summary>
    private void SetBadgeColors(Color fill)
    {
        _taskBadge.BackColor = Theme.Mix(Theme.Surface, fill, 0.14f);
        _taskBadge.ForeColor = fill;
    }

    private void UpdateDaemonLabel()
    {
        if (_preview) return;
        bool running = DaemonControl.IsRunning();
        bool curve = HasSavedFanCurve();
        bool undervolt = HasSavedUndervolt();

        if (_controlLease is not null)
        {
            _daemonLabel.Text = _fanSummary ?? "● 窗口接管控制：风扇每 4 秒调节，关闭后交还后台";
            _daemonLabel.ForeColor = Theme.Success;
            return;
        }

        if (_tempFanSettings.ContainsKey(SelectedIndex))
        {
            _daemonLabel.Text = "● 临时风扇控制运行中：关闭软件后恢复已保存配置";
            _daemonLabel.ForeColor = Theme.Accent;
            return;
        }

        if (_tempUndervoltGpus.Contains(SelectedIndex))
        {
            _daemonLabel.Text = "● 临时降压已应用：关闭软件后恢复已保存配置";
            _daemonLabel.ForeColor = Theme.Accent;
            return;
        }

        if (running && curve && undervolt)
        {
            _daemonLabel.Text = "● 后台运行中：风扇曲线每 4 秒调节，降压每 20 秒看护";
            _daemonLabel.ForeColor = Theme.Success;
        }
        else if (running && curve)
        {
            _daemonLabel.Text = "● 后台运行中：风扇曲线每 4 秒调节";
            _daemonLabel.ForeColor = Theme.Success;
        }
        else if (running && undervolt)
        {
            _daemonLabel.Text = "● 后台运行中：降压每 20 秒看护，睡眠唤醒后自动补回";
            _daemonLabel.ForeColor = Theme.Success;
        }
        else if (curve || undervolt)
        {
            _daemonLabel.Text = "○ 后台未运行（设置已保存，下次登录会自动应用）";
            _daemonLabel.ForeColor = Theme.Warning;
        }
        else
        {
            _daemonLabel.Text = "○ 自动 / 固定转速无需后台常驻";
            _daemonLabel.ForeColor = Theme.Muted;
        }
    }

    private void SetStatus(string message, Color color)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = color;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_preview) RestoreTemporaryChanges();
        _guiLease?.Dispose();
        _guiLease = null;
        _pollTimer.Stop();
        if (!_preview)
        {
            try { NvApi.Unload(); } catch { }
            try { FanApi.Shutdown(); } catch { }
        }
        base.OnFormClosed(e);
    }

    private void RestoreTemporaryChanges()
    {
        _fanTimer.Stop();
        if (_controlLease is null) return;
        try
        {
            // Read saved state directly. Never feed temporary values back into the restore loop.
            int[] fans = FanState.StoredGpuIndexes().Concat(_tempFanCleared).Concat(_tempFanSettings.Keys).Distinct().ToArray();
            _fanController.RestoreSaved(fans, FanRunner.LoadSaved);
            foreach (int index in _tempUndervoltGpus.Concat(_tempUndervoltCleared).Distinct())
            {
                if (index >= _gpus.Length || PersistedTuning.StoredGpuIndexes().Contains(index)) continue;
                try
                {
                    GpuBinding.Require(_gpuUuids.GetValueOrDefault(index), FanRunner.Uuid(_gpus[index]));
                    GpuTuning.Clear(_gpus[index]);
                }
                catch (Exception ex) { Diag.Log($"恢复 GPU {index} 默认电压失败: {ex.Message}"); }
            }
            foreach (int index in PersistedTuning.StoredGpuIndexes())
            {
                try { Daemon.RestoreSaved(index); }
                catch (Exception ex) { Diag.Log($"恢复 GPU {index} 已保存降压失败: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Diag.Log("恢复临时设置失败: " + ex.Message); }
        finally
        {
            _tempUndervoltGpus.Clear();
            _tempUndervoltCleared.Clear();
            _tempFanSettings.Clear();
            _tempFanCleared.Clear();
            _controlLease.Dispose();
            _controlLease = null;
        }
        try
        {
            DaemonControl.Start(recoverFans: true);
        }
        catch (Exception ex) { Diag.Log("重新启动后台失败: " + ex.Message); }
    }
}
