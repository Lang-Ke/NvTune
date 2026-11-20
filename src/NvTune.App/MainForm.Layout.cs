using System.Runtime.InteropServices;

namespace SimpleNvidiaUndervolt;

internal sealed partial class MainForm
{
    private Panel _viewport = null!;
    private TableLayoutPanel _content = null!;
    private readonly MetricTile[] _metrics =
    {
        new("GPU 温度", "°C", Theme.Accent), new("风扇读数", "%", Theme.AccentAlt),
        new("板卡功耗", "W", Theme.Warning), new("核心频率", "MHz", Theme.AccentAlt),
        new("核心电压", "mV", Theme.Accent),
    };
    private readonly ToolTip _tips = new();

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Canvas,
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, D(60)));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, D(30)));
        _viewport = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = Theme.Canvas };
        _content = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 4, Location = Point.Empty,
            Padding = new Padding(D(18), D(8), D(18), 0), Margin = Padding.Empty,
            BackColor = Theme.Canvas, Height = D(664), Width = D(960),
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 88, 78, 136 })
            _content.RowStyles.Add(new RowStyle(SizeType.Absolute, D(height)));
        _content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _content.Controls.Add(BuildHeader(), 0, 0);
        var metrics = Theme.Table(5, 1);
        metrics.BackColor = Theme.Canvas;
        metrics.Margin = new Padding(0, 0, 0, D(10));
        metrics.ColumnStyles.Clear();
        for (int i = 0; i < _metrics.Length; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            _metrics[i].Dock = DockStyle.Fill;
            _metrics[i].Margin = new Padding(0, 0, i == 4 ? 0 : D(12), 0);
            metrics.Controls.Add(_metrics[i], i, 0);
        }
        _content.Controls.Add(metrics, 0, 1);
        _content.Controls.Add(BuildUndervoltCard(), 0, 2);
        _content.Controls.Add(BuildFanCard(), 0, 3);
        _viewport.Controls.Add(_content);
        _viewport.SizeChanged += (_, _) => UpdateContentWidth();
        shell.Controls.Add(_viewport, 0, 0);
        shell.Controls.Add(BuildFooter(), 0, 1);
        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Margin = Padding.Empty;
        _statusLabel.Padding = new Padding(D(24), 0, D(24), 0);
        _statusLabel.Font = Theme.SmallFont();
        _statusLabel.AutoEllipsis = true;
        _statusLabel.BackColor = Theme.CanvasAlt;
        _statusLabel.Text = "就绪 · 临时应用用于测试，保存后下次登录自动应用";
        shell.Controls.Add(_statusLabel, 0, 2);
        Controls.Add(shell);
    }

    private void UpdateContentWidth()
    {
        if (_viewport is null || _content is null) return;
        int minimum = Theme.Pixels(this, 860);
        int width = Math.Max(minimum, _viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        if (_content.Width != width) _content.Width = width;
        int height = Math.Max(Theme.Pixels(this, 664), _viewport.ClientSize.Height);
        if (_content.Height != height) _content.Height = height;
    }

    private void FitToWorkingArea()
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;
        int inset = Theme.Pixels(this, 12);
        MinimumSize = new Size(Math.Min(Theme.Pixels(this, 640), Math.Max(320, area.Width - inset * 2)),
            Math.Min(Theme.Pixels(this, 480), Math.Max(240, area.Height - inset * 2)));
        if (WindowState != FormWindowState.Normal) return;
        Size = new Size(Math.Min(Width, area.Width - inset * 2), Math.Min(Height, area.Height - inset * 2));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, D(12)), BackColor = Theme.Canvas };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(192)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(148)));
        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, D(34)));
        brand.Controls.Add(new Label { Text = "NvTune", Font = Theme.TitleFont(20), ForeColor = Theme.Text, Dock = DockStyle.Fill, Margin = Padding.Empty });
        brand.Controls.Add(new Label { Text = "NVIDIA 显卡调校中心", Font = Theme.SmallFont(), ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(D(2), 0, 0, 0) });
        panel.Controls.Add(brand, 0, 0);
        var selector = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(D(12), 0, D(18), 0) };
        selector.RowStyles.Add(new RowStyle(SizeType.Absolute, D(24)));
        selector.Controls.Add(Theme.Caption("当前显卡"), 0, 0);
        _gpuSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gpuSelector.Height = D(36);
        _gpuSelector.SelectedIndexChanged += (_, _) => OnGpuChanged();
        selector.Controls.Add(_gpuSelector, 0, 1);
        panel.Controls.Add(selector, 1, 0);
        _taskBadge.Anchor = AnchorStyles.Right;
        _taskBadge.Text = "未保存配置";
        _taskBadge.Width = D(142);
        _taskBadge.Height = D(28);
        _taskBadge.BackColor = Theme.CanvasAlt;
        _taskBadge.ForeColor = Theme.Muted;
        panel.Controls.Add(_taskBadge, 2, 0);
        return panel;
    }
    private Control BuildUndervoltCard()
    {
        var card = Theme.Card("01   核心降压", out var body);
        var grid = Theme.Table(5, 2);
        grid.ColumnStyles.Clear();
        for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(142)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(126)));
        grid.RowStyles.Clear();
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, D(24)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, D(38)));
        AddField(grid, 0, "电压上限 · mV", _mvInput);
        AddField(grid, 1, "目标频率 · MHz", _mhzInput);
        AddField(grid, 2, "显存偏移 · MHz", _memInput);
        var apply = Theme.PrimaryButton("本次应用", (_, _) => ApplyUndervolt());
        _tips.SetToolTip(apply, "临时应用降压，关闭软件后恢复保存的配置");
        grid.Controls.Add(Theme.Cell(apply, new Padding(D(12), 0, 0, 0)), 3, 1);
        grid.Controls.Add(Theme.Cell(Theme.SecondaryButton("恢复默认", (_, _) => ResetUndervolt()), new Padding(D(12), 0, 0, 0)), 4, 1);
        body.Controls.Add(grid);
        return card;
    }

    private Control BuildFanCard()
    {
        var card = Theme.Card("02   风扇曲线", out var body);
        var top = Theme.Table(6, 1);
        top.Dock = DockStyle.Top;
        top.Height = D(44);
        top.ColumnStyles.Clear();
        foreach (int width in new[] { 154, 130, 142, 126 }) top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(width)));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(126)));
        top.RowStyles.Clear(); top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.Controls.Add(Theme.Cell(_fanMode, new Padding(0, 0, D(12), 0)), 0, 0);
        _fanMode.Width = D(142);
        var fixedHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        fixedHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(88)));
        fixedHost.Controls.Add(Theme.Cell(_fanFixed, Padding.Empty), 0, 0);
        _fanFixed.Width = D(88);
        fixedHost.Controls.Add(Theme.CellLabel("%", new Padding(D(6), 0, 0, 0)), 1, 0);
        _tips.SetToolTip(_fanFixed, "固定转速模式下使用");
        top.Controls.Add(fixedHost, 1, 0);
        top.Controls.Add(Theme.Cell(Theme.PrimaryButton("本次应用", (_, _) => ApplyFanSettings()), new Padding(0, 0, D(10), 0)), 2, 0);
        top.Controls.Add(Theme.Cell(Theme.SecondaryButton("恢复自动", (_, _) => RestoreFanAuto()), Padding.Empty), 3, 0);
        top.Controls.Add(Theme.Cell(Theme.SecondaryButton("推荐曲线", (_, _) => _curveEditor.ResetToDefault()), Padding.Empty), 5, 0);
        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = D(20), Text = "双击添加 / 删除控制点     拖动调整     方向键微调 · Shift ×5",
            Font = Theme.SmallFont(), ForeColor = Theme.Muted, BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        _daemonLabel.Dock = DockStyle.Bottom;
        _daemonLabel.AutoSize = false;
        _daemonLabel.Height = D(20);
        _daemonLabel.AutoEllipsis = true;
        _daemonLabel.Font = Theme.SmallFont();
        _curveEditor.MinimumSize = new Size(D(360), D(170));
        _curveEditor.SetPoints(FanCurve.Default.Points);
        body.Controls.Add(_curveEditor);
        body.Controls.Add(hint);
        body.Controls.Add(_daemonLabel);
        body.Controls.Add(top);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = Theme.Table(4, 1);
        footer.BackColor = Theme.Canvas;
        footer.Padding = new Padding(D(18), D(10), D(18), D(10));
        footer.ColumnStyles.Clear();
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(152)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(142)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(142)));
        footer.RowStyles.Clear(); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(Theme.Cell(Theme.PrimaryButton("保存并开机应用", (_, _) => SaveAllSettings(), 152), Padding.Empty), 0, 0);
        footer.Controls.Add(new Label { Text = "Ctrl + S", ForeColor = Theme.Muted, Font = Theme.SmallFont(), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(D(14), 0, 0, 0) }, 1, 0);
        var reset = Theme.SecondaryButton("全部恢复出厂", (_, _) => ClearEverything(), 132);
        reset.ForeColor = Theme.Danger;
        footer.Controls.Add(Theme.Cell(reset, new Padding(D(10), 0, 0, 0)), 2, 0);
        footer.Controls.Add(Theme.Cell(Theme.SecondaryButton("打开数据目录", (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Persistence.DataDir()) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus(ex.Message, Theme.Danger); }
        }, 132), new Padding(D(10), 0, 0, 0)), 3, 0);
        return footer;
    }

    private static void AddField(TableLayoutPanel grid, int column, string caption, Control input)
    {
        var label = Theme.Caption(caption);
        label.Margin = Padding.Empty;
        grid.Controls.Add(label, column, 0);
        input.Dock = DockStyle.None;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Height = D(36);
        input.Margin = new Padding(0, 0, D(16), 0);
        grid.Controls.Add(input, column, 1);
    }

    private void UpdateMetrics(uint temp, uint fan, uint watts, uint clock, uint mv)
    {
        uint[] values = { temp, fan, watts, clock, mv };
        for (int i = 0; i < values.Length; i++) _metrics[i].Value = values[i].ToString();
    }

    internal void PopulatePreview()
    {
        _loading = true;
        _gpuSelector.Items.Add("NVIDIA GeForce RTX 4070 SUPER");
        _gpuSelector.SelectedIndex = 0;
        _fanMode.SelectedIndex = 2;
        _mvInput.Value = 950; _mhzInput.Value = 2700; _memInput.Value = 500;
        _loading = false;
        _curveEditor.SetPoints(FanCurve.Default.Points);
        _curveEditor.CurrentTemp = 57;
        UpdateMetrics(57, 42, 148, 2700, 950);
        _metrics[1].Value = "1200";
        _metrics[1].Unit = "RPM";
        _daemonLabel.Text = "设定 42% · F1 读数 45% · 目标 42% · 1200 RPM · 手动";
        _daemonLabel.ForeColor = Theme.Accent;
        _taskBadge.Text = "配置已保存";
        SetBadgeColors(Theme.Success);
        _statusLabel.Text = "就绪 · 临时应用用于测试，保存后下次登录自动应用";
        _statusLabel.ForeColor = Theme.Muted;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int enabled = 1; DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); } catch (DllNotFoundException) { }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _pollTimer.Dispose();
            _fanTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
