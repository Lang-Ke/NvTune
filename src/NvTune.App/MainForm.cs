using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

internal sealed class MainForm : Form
{
    private static int D(int px) => Theme.Scaled(px);

    private readonly ComboBox _gpuSelector = Theme.Combo(340);
    private readonly Label _gpuInfo = new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.SmallFont(),
        Margin = new Padding(D(2), D(3), 0, 0),
    };
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

    private readonly ComboBox _fanMode = Theme.Combo(140);
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

    private IntPtr[] _gpus = Array.Empty<IntPtr>();
    private IReadOnlyList<(int Mv, int Mhz)> _anchors = Array.Empty<(int, int)>();
    private bool _loading;

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

    public MainForm(string[] args)
    {
        Text = "NvTune · NVIDIA 降压与风扇";
        Font = Theme.BodyFont();
        BackColor = Theme.Canvas;
        // Every fixed pixel value in this form goes through Theme.Scaled(). Letting WinForms also
        // apply its own DPI autoscale would multiply the two factors and blow the layout apart on
        // a 175% display, so the scaling has exactly one owner.
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ResizeRedraw = true;
        // On a small screen at 250% the design layout cannot fit, so the window scrolls instead of
        // squeezing the cards until their rows overlap.
        AutoScroll = true;
        MinimumSize = new Size(D(880), D(600));
        Size = new Size(D(1140), D(900));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(D(18), D(14), D(18), 0),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(100)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(132)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(64)));
        // The floor keeps every card at its design height; without it a short window shrinks the
        // percent row below the fan card's minimum and the heading paints over the plot.
        root.MinimumSize = new Size(D(880) - D(36), D(14) + D(100) + D(132) + D(360) + D(64));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildUndervoltCard(), 0, 1);
        root.Controls.Add(BuildFanCard(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
        Controls.Add(_statusLabel);

        _statusLabel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _statusLabel.Width, 0);
        };

        _fanMode.Items.AddRange(new object[] { "自动", "固定转速", "温度曲线" });
        _pollTimer.Tick += (_, _) => UpdateLiveStatus();
        _curveEditor.PointsChanged += (_, _) => MarkFanDirty();
        _fanMode.SelectedIndexChanged += (_, _) => OnFanModeChanged();
        _mvInput.ValueChanged += (_, _) => OnVoltageEdited();
        _mhzInput.ValueChanged += (_, _) => MarkUndervoltDirty();
        _memInput.ValueChanged += (_, _) => MarkUndervoltDirty();
        Load += (_, _) =>
        {
            SetStatus("正在初始化显卡接口…", Theme.Muted);
            _ = InitializeGpusAsync();
        };
        Shown += (_, _) => ForcePaint();
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = D(92),
            BackColor = Theme.Canvas,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(D(4), D(4), D(4), D(8)),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, D(26), 0) };
        brand.Controls.Add(new Label
        {
            Text = "NvTune",
            Font = Theme.TitleFont(19f),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, D(2), 0, 0),
        });
        brand.Controls.Add(new Label
        {
            Text = "NVIDIA 显卡降压 · 多点风扇曲线",
            Font = Theme.SmallFont(),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Margin = new Padding(D(1), D(1), 0, 0),
        });

        _gpuSelector.SelectedIndexChanged += (_, _) => OnGpuChanged();
        var gpuStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, D(8), 0, 0),
        };
        gpuStack.Controls.Add(Theme.Caption("选择显卡"));
        gpuStack.Controls.Add(_gpuSelector);
        gpuStack.Controls.Add(_gpuInfo);

        _taskBadge.Margin = new Padding(0, 0, D(4), 0);
        panel.Controls.Add(brand, 0, 0);
        panel.Controls.Add(gpuStack, 1, 0);
        panel.Controls.Add(_taskBadge, 2, 0);
        return panel;
    }

    private Control BuildUndervoltCard()
    {
        var card = Theme.Card("核心降压", out var cardBody);
        // A two-row grid keeps every caption on one baseline and every input and button on the
        // next, so nothing drifts when a caption wraps or a control changes height.
        var grid = Theme.Table(5, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, D(40)));
        AddField(grid, 0, "电压上限 (mV)", _mvInput);
        AddField(grid, 1, "该电压目标频率 (MHz)", _mhzInput);
        AddField(grid, 2, "显存偏移 (MHz)", _memInput);

        var applyUndervolt = Theme.PrimaryButton("本次应用", (_, _) => ApplyUndervolt());
        new ToolTip().SetToolTip(applyUndervolt, "仅本次运行临时应用降压，关闭软件后恢复已保存配置");
        grid.Controls.Add(Theme.Cell(applyUndervolt, new Padding(D(18), 0, 0, 0)), 3, 1);
        grid.Controls.Add(Theme.Cell(Theme.SecondaryButton("恢复默认", (_, _) => ResetUndervolt()),
            new Padding(D(10), 0, 0, 0)), 4, 1);

        cardBody.Controls.Add(grid);
        return card;
    }

    private Control BuildFanCard()
    {
        var card = Theme.Card("风扇控制", out var fanBody);
        var top = Theme.Table(9, 1);
        top.Dock = DockStyle.Top;
        top.Height = D(56);
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        top.Controls.Add(Theme.CellLabel("模式", new Padding(0, 0, D(8), 0)), 0, 0);
        top.Controls.Add(Theme.Cell(_fanMode, new Padding(0, 0, D(20), 0)), 1, 0);
        top.Controls.Add(Theme.CellLabel("固定转速", new Padding(0, 0, D(8), 0)), 2, 0);
        top.Controls.Add(Theme.Cell(_fanFixed, new Padding(0, 0, D(4), 0)), 3, 0);
        top.Controls.Add(Theme.CellLabel("%", new Padding(0, 0, D(20), 0)), 4, 0);

        var applyFan = Theme.PrimaryButton("本次应用", (_, _) => ApplyFanSettings());
        new ToolTip().SetToolTip(applyFan, "仅本次运行临时应用风扇设置，关闭软件后恢复已保存配置");
        // The left margin matches the undervolt row's button offset, so the two "本次应用"
        // buttons stack on one vertical line instead of drifting by a few pixels.
        top.Controls.Add(Theme.Cell(applyFan, new Padding(D(10), 0, D(10), 0)), 5, 0);
        top.Controls.Add(Theme.Cell(Theme.SecondaryButton("恢复默认", (_, _) => RestoreFanAuto()),
            new Padding(0, 0, D(22), 0)), 6, 0);
        top.Controls.Add(Theme.CellLabel("推荐曲线", new Padding(0, 0, D(8), 0)), 7, 0);
        top.Controls.Add(Theme.Cell(Theme.SecondaryButton("载入", (_, _) => _curveEditor.ResetToDefault(), 78),
            new Padding(0)), 8, 0);

        var editorHost = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, D(2), 0, 0),
            BackColor = Theme.Surface,
        };
        editorHost.Controls.Add(_curveEditor);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = D(40),
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(D(2), D(8), 0, 0),
            BackColor = Theme.Surface,
        };
        bottom.Controls.Add(_daemonLabel);
        // Dock order matters: the fill area must be added first so the top strip and the
        // status line dock before it, instead of the status line overlapping the plot.
        fanBody.Controls.Add(editorHost);
        fanBody.Controls.Add(bottom);
        fanBody.Controls.Add(top);
        return card;
    }

    private Control BuildFooter()
    {
        var flow = Theme.Table(4, 1);
        flow.Height = D(60);
        flow.Padding = new Padding(D(4), D(8), 0, D(8));
        flow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var save = Theme.PrimaryButton("写入配置", (_, _) => SaveAllSettings(), 132);
        var tip = new ToolTip();
        tip.SetToolTip(save, "把当前降压参数与风扇设置写入配置文件，重启后自动生效");
        flow.Controls.Add(Theme.Cell(save, new Padding(0, 0, D(6), 0)), 0, 0);
        flow.Controls.Add(Theme.CellLabel("重启生效", new Padding(0, 0, D(18), 0)), 1, 0);
        flow.Controls.Add(Theme.Cell(Theme.SecondaryButton("全部恢复出厂", (_, _) => ClearEverything(), 132),
            new Padding(0, 0, D(8), 0)), 2, 0);
        flow.Controls.Add(Theme.Cell(Theme.SecondaryButton("打开数据目录", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Persistence.DataDir()) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("打开目录失败: " + ex.Message, Theme.Danger); }
        }, 132), new Padding(0)), 3, 0);
        return flow;
    }

    private static void AddField(TableLayoutPanel grid, int column, string caption, Control input)
    {
        Label label = Theme.Caption(caption);
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 0, D(18), D(4));
        grid.Controls.Add(label, column, 0);
        grid.Controls.Add(Theme.Cell(input, new Padding(0, 0, D(18), 0)), column, 1);
    }

    private async Task InitializeGpusAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                NvApi.Initialize();
                FanRunner.Initialize(NvApi.EnumeratePhysicalGpus().Length);
                _gpus = NvApi.EnumeratePhysicalGpus();
            });
            if (_gpus.Length == 0)
            {
                SetStatus("未检测到 NVIDIA 显卡。", Theme.Danger);
                return;
            }

            _loading = true;
            foreach (IntPtr gpu in _gpus) _gpuSelector.Items.Add(NvApi.SafeFullName(gpu));
            _gpuSelector.SelectedIndex = 0;
            _loading = false;
            await Task.Run(() => _anchors = NvApi.GetVfCurve(SelectedGpuHandle));
            OnGpuChanged();
            _pollTimer.Start();
            SetStatus("就绪。「写入配置」重启后仍然生效；两处「本次应用」仅在本次运行期间生效。", Theme.Success);
            UpdateTaskBadge();
        }
        catch (Exception ex)
        {
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

    /// <summary>A move to a monitor with a different scale must re-derive every fixed pixel value,
    /// so the cached DPI ratio is dropped before the new layout pass runs.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        float previous = Theme.DpiScale;
        Theme.ResetDpiCache(e.DeviceDpiNew);
        float current = Theme.DpiScale;
        if (previous > 0 && Math.Abs(current - previous) > 0.01f)
        {
            float ratio = current / previous;
            MinimumSize = new Size(
                Math.Max(1, (int)Math.Round(MinimumSize.Width * ratio)),
                Math.Max(1, (int)Math.Round(MinimumSize.Height * ratio)));
            RescaleForDpi(this, current / previous);
        }
        base.OnDpiChanged(e);
    }

    /// <summary>The form opts out of WinForms autoscaling so the theme owns every pixel value, which
    /// means a monitor change has to rescale the tree by hand. Ratios are applied to sizes,
    /// margins and padding; fonts are in points and already follow the new DPI.</summary>
    private static void RescaleForDpi(Control parent, float ratio)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Dock == DockStyle.Fill || child.Dock == DockStyle.Bottom || child.Dock == DockStyle.Top
                || child.Dock == DockStyle.Left || child.Dock == DockStyle.Right)
            {
                // A docked control takes its extent from the parent, but its fixed side and
                // padding still carry design pixels.
                if (child.Dock is DockStyle.Top or DockStyle.Bottom)
                {
                    child.Height = Math.Max(1, (int)Math.Round(child.Height * ratio));
                }
                else if (child.Dock is DockStyle.Left or DockStyle.Right)
                {
                    child.Width = Math.Max(1, (int)Math.Round(child.Width * ratio));
                }
            }
            else
            {
                child.Width = Math.Max(1, (int)Math.Round(child.Width * ratio));
                child.Height = Math.Max(1, (int)Math.Round(child.Height * ratio));
            }

            child.Margin = ScalePadding(child.Margin, ratio);
            child.Padding = ScalePadding(child.Padding, ratio);
            if (child.MinimumSize.Width > 0 || child.MinimumSize.Height > 0)
            {
                child.MinimumSize = new Size(
                    Math.Max(0, (int)Math.Round(child.MinimumSize.Width * ratio)),
                    Math.Max(0, (int)Math.Round(child.MinimumSize.Height * ratio)));
            }

            if (child is TableLayoutPanel table)
            {
                foreach (RowStyle row in table.RowStyles)
                {
                    if (row.SizeType == SizeType.Absolute) row.Height = Math.Max(1, row.Height * ratio);
                }
                foreach (ColumnStyle column in table.ColumnStyles)
                {
                    if (column.SizeType == SizeType.Absolute) column.Width = Math.Max(1, column.Width * ratio);
                }
            }

            RescaleForDpi(child, ratio);
        }
    }

    private static Padding ScalePadding(Padding padding, float ratio) => new(
        (int)Math.Round(padding.Left * ratio), (int)Math.Round(padding.Top * ratio),
        (int)Math.Round(padding.Right * ratio), (int)Math.Round(padding.Bottom * ratio));

    /// <summary>WS_EX_COMPOSITED makes Windows compose the whole window off-screen before it is
    /// blitted. Without it, dragging or resizing leaves half-painted cards and plot fragments
    /// behind, because the custom-painted children repaint at different moments.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    private int SelectedIndex => Math.Max(0, _gpuSelector.SelectedIndex);
    private IntPtr SelectedGpuHandle => _gpus.Length > SelectedIndex ? _gpus[SelectedIndex] : IntPtr.Zero;
    private bool UndervoltDirty => _dirtyUndervoltGpus.Contains(SelectedIndex);

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
        bool hasDraft = _undervoltDrafts.TryGetValue(SelectedIndex, out UndervoltDraft? draft);
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
        if (!hasDraft)
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
        SetStatus("降压参数已修改。点「本次应用」仅本次运行生效，点「写入配置」重启后仍生效。", Theme.Warning);
    }

    private void ApplyUndervolt()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero) return;
        try
        {
            DaemonControl.Stop();
            var stock = GpuTuning.ResetAndReadStock(gpu);
            int mv = (int)_mvInput.Value, mhz = (int)_mhzInput.Value, memDelta = (int)_memInput.Value * 1000;
            var plan = GpuTuning.BuildCurvePlan(stock, mv, mhz, TuneRequest.DefaultCapPoints);
            GpuTuning.Apply(gpu, plan, stock, mhz, memDelta);
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

    /// <summary>Saves the current panel state without touching the GPU when it can: the undervolt
    /// plan is recomputed from a read-only stock recovery, and only a recovery the driver distorts
    /// falls back to a real reset. The background task then applies what was saved.</summary>
    private void SaveAllSettings()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero)
        {
            SetStatus("没有可保存的显卡。", Theme.Warning);
            return;
        }

        try
        {
            Persistence.EnsureInstalled();
            bool savedUndervolt = false;
            if (_tempUndervoltCleared.Contains(SelectedIndex))
            {
                PersistedTuning.Remove(SelectedIndex);
                UndervoltUiState.Delete(SelectedIndex);
                _dirtyUndervoltGpus.Remove(SelectedIndex);
                _tempUndervoltCleared.Remove(SelectedIndex);
                savedUndervolt = true;
            }
            else if (UndervoltDirty || _tempUndervoltGpus.Contains(SelectedIndex))
            {
                var stock = ReadStockForSave(gpu);
                int mv = (int)_mvInput.Value, mhz = (int)_mhzInput.Value, memDelta = (int)_memInput.Value * 1000;
                var plan = GpuTuning.BuildCurvePlan(stock, mv, mhz, TuneRequest.DefaultCapPoints);
                var doc = TuningDocuments.MakeTuningDoc(GpuIdentity.Read(gpu), stock,
                    new AppliedTuning(plan.DeltasKhz, memDelta));
                PersistedTuning.Save(doc, SelectedIndex);
                UndervoltUiState.Save(new UndervoltUiState.Values(mv, mhz, (int)_memInput.Value), SelectedIndex);
                _dirtyUndervoltGpus.Remove(SelectedIndex);
                _tempUndervoltGpus.Remove(SelectedIndex);
                savedUndervolt = true;
            }

            int mode = _fanMode.SelectedIndex;
            if (mode == 1)
            {
                int percent = (int)_fanFixed.Value;
                FanState.Save(new FanState.FanSettings(1, percent, null), SelectedIndex);
                // A fixed speed has no curve to poll, so the daemon exits; write it now or the
                // setting would only appear at the next logon.
                if (FanRunner.TryDevice(SelectedIndex, out IntPtr fanDev) && FanRunner.FanCount(fanDev) > 0)
                {
                    FanRunner.SetAllFans(fanDev, (uint)Math.Clamp(percent, 0, 100));
                }
            }
            else if (mode == 2)
            {
                FanState.Save(new FanState.FanSettings(2, null,
                    _curveEditor.GetPoints().Select(p => new[] { p.TempC, p.Percent }).ToArray()), SelectedIndex);
            }
            else
            {
                FanState.Delete(SelectedIndex);
                if (FanRunner.TryDevice(SelectedIndex, out IntPtr autoDev)) FanRunner.RestoreAuto(autoDev);
            }

            UndervoltUiState.Save(new UndervoltUiState.Values(
                (int)_mvInput.Value, (int)_mhzInput.Value, (int)_memInput.Value), SelectedIndex);

            _tempFanSettings.Remove(SelectedIndex);
            _tempFanCleared.Remove(SelectedIndex);

            SyncSchedulerToSavedSettings();
            // A newly saved undervolt should take effect now, not on the watchdog's next 20 s
            // sweep: the daemon re-reads every stored document when it starts.
            if (savedUndervolt) DaemonControl.Stop();
            SyncDaemonToSavedSettings();
            UpdateTaskBadge();
            UpdateDaemonLabel();

            bool hasAnything = HasSavedUndervolt() || HasSavedFanCurve()
                || FanState.StoredGpuIndexes().Count > 0;
            string pendingElsewhere = _dirtyUndervoltGpus.Count > 0
                ? " 另有显卡的降压改动尚未保存，请切到该卡再保存。"
                : string.Empty;
            SetStatus((!hasAnything
                ? "当前为自动风扇、无降压设置，没有需要保存的配置。"
                : savedUndervolt
                    ? "配置已写入，重启后自动生效。"
                    : "风扇配置已写入，重启后自动生效；降压参数未修改，保留原降压配置。") + pendingElsewhere,
                hasAnything ? Theme.Success : Theme.Muted);
        }
        catch (Exception ex)
        {
            SetStatus("写入配置失败: " + ex.Message, Theme.Danger);
            Diag.Log("写入配置失败: " + ex);
            MessageBox.Show(this, ex.Message, "写入配置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateLiveStatus();
    }

    /// <summary>Read-only when the driver lets it be; a card already holding a tuning distorts the
    /// recovered stock curve, and a reset there is safe because the saved plan is applied again
    /// right afterwards.</summary>
    private static IReadOnlyList<(int Mv, int Mhz)> ReadStockForSave(IntPtr gpu)
    {
        try
        {
            return GpuTuning.RecoverStockReadOnly(gpu);
        }
        catch (CliError)
        {
            return GpuTuning.ResetAndReadStock(gpu);
        }
    }

    private void ResetUndervolt()
    {
        try
        {
            DaemonControl.Stop();
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
        var settings = FanState.Load(SelectedIndex);
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
            SetStatus("曲线已修改。点「本次应用」仅本次运行生效，点「写入配置」重启后仍生效。", Theme.Warning);
        }
    }

    private void ApplyFanSettings()
    {
        int mode = _fanMode.SelectedIndex;
        if (mode == 0) { RestoreFanAuto(); return; }

        try
        {
            IntPtr dev = FanRunner.Device(SelectedIndex);
            if (FanRunner.FanCount(dev) == 0) { SetStatus("此显卡没有可通过软件控制的风扇。", Theme.Warning); return; }

            var settings = mode == 1
                ? new FanState.FanSettings(mode, (int)_fanFixed.Value, null)
                : new FanState.FanSettings(mode, null, _curveEditor.GetPoints().Select(p => new[] { p.TempC, p.Percent }).ToArray());
            int target = mode == 1 ? settings.FixedPercent!.Value : settings.Curve!.FanPercentAt((int)FanRunner.TemperatureC(dev));
            FanRunner.SetAllFans(dev, (uint)Math.Clamp(target, 0, 100));
            DaemonControl.Stop();
            _tempFanCleared.Remove(SelectedIndex);
            _tempFanSettings[SelectedIndex] = settings;
            if (mode == 2) _fanTimer.Start();
            SetStatus(mode == 2 ? "风扇曲线已临时应用，关闭软件后恢复已保存配置。" : $"风扇已临时固定 {target}%，关闭软件后恢复已保存配置。", Theme.Success);
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
        try
        {
            if (FanRunner.TryDevice(SelectedIndex, out IntPtr dev)) FanRunner.RestoreAuto(dev);
            DaemonControl.Stop();
            _tempFanSettings.Remove(SelectedIndex);
            _tempFanCleared.Add(SelectedIndex);
            _fanTimer.Stop();
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
        if (!_tempFanSettings.TryGetValue(SelectedIndex, out var settings)
            || settings.ModeValue != FanState.Mode.Curve
            || !FanRunner.TryDevice(SelectedIndex, out IntPtr dev))
        {
            return;
        }

        try
        {
            int target = settings.Curve!.FanPercentAt((int)FanRunner.TemperatureC(dev));
            FanRunner.SetAllFans(dev, (uint)Math.Clamp(target, 0, 100));
        }
        catch { }
    }

    private void ClearEverything()
    {
        if (MessageBox.Show(this, "将清除所有降压与风扇设置，恢复显卡默认状态，并删除开机任务和后台进程。\n\n继续？",
                "全部恢复出厂", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            DaemonControl.Stop();
            foreach (IntPtr gpu in _gpus) if (gpu != IntPtr.Zero) GpuTuning.Clear(gpu);
            int count = FanApi.GetDeviceCount();
            for (int i = 0; i < count; i++) if (FanRunner.TryDevice(i, out IntPtr dev)) FanRunner.RestoreAuto(dev);
            foreach (int index in FanState.StoredGpuIndexes()) FanState.Delete(index);
            int[] tuningIndexes = PersistedTuning.StoredGpuIndexes().ToArray();
            foreach (int index in tuningIndexes) PersistedTuning.Remove(index);
            foreach (int index in tuningIndexes) UndervoltUiState.Delete(index);
            Scheduler.Remove();
            Scheduler.RemoveLegacyTasks();
            _dirtyUndervoltGpus.Clear();
            _undervoltDrafts.Clear();
            LoadFanStateForGpu();
            ConfigureVoltageRange();
            SetStatus("已全部恢复出厂，开机任务与后台进程已清除。", Theme.Success);
            UpdateTaskBadge();
            UpdateDaemonLabel();
        }
        catch (Exception ex) { SetStatus("清除失败: " + ex.Message, Theme.Danger); }
    }

    private void SyncSchedulerToSavedSettings()
    {
        bool hasSettings = PersistedTuning.StoredGpuIndexes().Count > 0 || FanState.StoredGpuIndexes().Count > 0;
        if (hasSettings) Scheduler.Register(); else Scheduler.Remove();
    }

    /// <summary>
    /// One place decides whether the background task should run: a saved undervolt needs a
    /// watchdog, a temperature curve needs a poller, and a fixed fan speed needs neither.
    /// </summary>
    private void SyncDaemonToSavedSettings()
    {
        if (HasSavedUndervolt() || HasSavedFanCurve()) DaemonControl.Start();
        else DaemonControl.Stop();
    }

    private static bool HasSavedUndervolt() => PersistedTuning.StoredGpuIndexes().Count > 0;

    private static bool HasSavedFanCurve()
        => FanState.StoredGpuIndexes().Any(i => FanState.Load(i)?.ModeValue == FanState.Mode.Curve);

    private void UpdateLiveStatus()
    {
        IntPtr gpu = SelectedGpuHandle;
        if (gpu == IntPtr.Zero || !FanRunner.TryDevice(SelectedIndex, out IntPtr dev)) return;
        try
        {
            uint temp = FanRunner.TemperatureC(dev);
            uint fan = FanRunner.FanPercent(dev, 0);
            uint power = FanRunner.PowerMilliwatts(dev);
            uint coreMhz = FanRunner.CoreMhz(dev);
            uint mv = NvApi.GetCoreVoltageUv(gpu) / 1000;
            _curveEditor.CurrentTemp = (int)temp;
            _gpuInfo.Text = $"温度 {temp}°C    风扇 {fan}%    功耗 {power / 1000}W    核心 {coreMhz} MHz    电压 {mv} mV";
            UpdateDaemonLabel();
        }
        catch { }
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
            _taskBadge.Text = "✓  重启自动生效";
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
        _taskBadge.BackColor = fill;
        _taskBadge.ForeColor = Color.White;
    }

    private void UpdateDaemonLabel()
    {
        bool running = DaemonControl.IsRunning();
        bool curve = HasSavedFanCurve();
        bool undervolt = HasSavedUndervolt();

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = e.SuppressKeyPress = true;
            SaveAllSettings();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RestoreTemporaryChanges();
        _pollTimer.Stop();
        try { NvApi.Unload(); } catch { }
        try { FanApi.Shutdown(); } catch { }
        base.OnFormClosed(e);
    }

    private void RestoreTemporaryChanges()
    {
        _fanTimer.Stop();

        foreach (int index in _tempFanCleared.Concat(_tempFanSettings.Keys).Distinct())
        {
            try
            {
                var saved = FanState.Load(index);
                if (saved is null || saved.ModeValue == FanState.Mode.Auto)
                {
                    if (FanRunner.TryDevice(index, out IntPtr autoDev)) FanRunner.RestoreAuto(autoDev);
                }
                else
                {
                    _tempFanSettings[index] = saved;
                }
            }
            catch (Exception ex) { Diag.Log("恢复临时风扇设置失败: " + ex.Message); }
        }

        foreach (var (index, settings) in _tempFanSettings)
        {
            try
            {
                if (!FanRunner.TryDevice(index, out IntPtr dev) || FanRunner.FanCount(dev) == 0) continue;
                if (settings.ModeValue == FanState.Mode.Fixed)
                {
                    FanRunner.SetAllFans(dev, (uint)Math.Clamp(settings.FixedPercent ?? 50, 0, 100));
                }
                else if (settings.Curve is { } curve)
                {
                    FanRunner.SetAllFans(dev, (uint)Math.Clamp(curve.FanPercentAt((int)FanRunner.TemperatureC(dev)), 0, 100));
                }
            }
            catch (Exception ex) { Diag.Log("恢复已保存风扇设置失败: " + ex.Message); }
        }

        try
        {
            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            foreach (int index in _tempUndervoltGpus.Concat(_tempUndervoltCleared).Distinct())
            {
                if (index >= gpus.Length || PersistedTuning.StoredGpuIndexes().Contains(index)) continue;
                try { GpuTuning.Clear(gpus[index]); } catch (Exception ex) { Diag.Log($"恢复 GPU {index} 默认电压失败: {ex.Message}"); }
            }

            foreach (int index in PersistedTuning.StoredGpuIndexes())
            {
                if (index >= gpus.Length) continue;
                try
                {
                    var doc = PersistedTuning.Load(index);
                    var live = NvApi.GetVfCurve(gpus[index]);
                    if (!GpuTuning.CurveVoltsPlausible(live)) continue;
                    var reference = ReferenceCurve.Match(new SelectedGpu(gpus[index], index, gpus.Length));
                    GpuTuning.ApplyExact(gpus[index], doc, reference.Curve, live, $"GPU {index} 的已保存降压");
                }
                catch (Exception ex) { Diag.Log($"恢复 GPU {index} 已保存降压失败: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Diag.Log("恢复临时降压失败: " + ex.Message); }

        _tempUndervoltGpus.Clear();
        _tempUndervoltCleared.Clear();
        _tempFanSettings.Clear();
        _tempFanCleared.Clear();

        if (HasSavedUndervolt() || HasSavedFanCurve()) DaemonControl.Start();
    }
}
