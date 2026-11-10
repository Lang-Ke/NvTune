using System.Drawing.Drawing2D;

namespace SimpleNvidiaUndervolt;

/// <summary>The multi-point fan curve editor. It deliberately uses the standard Windows pointer:
/// dragging and double-clicking are enough of an affordance, so no custom cursor is set.</summary>
internal sealed class FanCurveEditor : Control
{
    private const int MinTemp = 20;
    private const int MaxTemp = 100;
    private const int MaxPoints = 12;
    private readonly List<(int TempC, int Percent)> _points = new();
    private int? _dragIndex;
    private int? _hoverIndex;
    private int? _selectedIndex;
    private int _currentTemp = -1;

    public FanCurveEditor()
    {
        DoubleBuffered = true;
        BackColor = Theme.PlotBackground;
        ForeColor = Theme.Text;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.Selectable, true);
        TabStop = true;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => { _dragIndex = null; Invalidate(); };
        MouseDoubleClick += OnMouseDoubleClick;
        MouseLeave += (_, _) => { _hoverIndex = null; Invalidate(); };
        Leave += (_, _) => { _selectedIndex = null; Invalidate(); };
    }

    public event EventHandler? PointsChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CurrentTemp
    {
        get => _currentTemp;
        set { if (_currentTemp != value) { _currentTemp = value; Invalidate(); } }
    }

    public IReadOnlyList<(int TempC, int Percent)> GetPoints() => _points.OrderBy(p => p.TempC).ToArray();

    public void SetPoints(IEnumerable<(int TempC, int Percent)> points)
    {
        _points.Clear();
        _points.AddRange(points.OrderBy(p => p.TempC).Take(MaxPoints));
        _selectedIndex = null;
        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefault() => SetPoints(FanCurve.Default.Points);

    private static int S(int px) => Theme.Scaled(px);

    private RectangleF PlotRect
    {
        get
        {
            int left = Math.Max(S(52), (int)(Width * 0.07f));
            int top = S(26);
            int rightInset = Math.Max(S(88), (int)(Width * 0.12f));
            int bottomInset = S(64);
            int plotWidth = Math.Max(S(60), Width - left - rightInset);
            int plotHeight = Math.Max(S(60), Height - top - bottomInset);
            return new RectangleF(left, top, plotWidth, plotHeight);
        }
    }

    private int TempAtX(float x) => (int)Math.Clamp(MinTemp + (x - PlotRect.X) / PlotRect.Width * (MaxTemp - MinTemp), MinTemp, MaxTemp);
    private int PctAtY(float y) => (int)Math.Clamp(100 - (y - PlotRect.Y) / PlotRect.Height * 100, 0, 100);
    private float XAtTemp(int t) => PlotRect.X + (t - MinTemp) * PlotRect.Width / (MaxTemp - MinTemp);
    private float YAtPct(int p) => PlotRect.Y + (100 - p) * PlotRect.Height / 100f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.PlotBackground);

        // Every brush and pen is owned by one using block: the earlier code allocated brushes
        // inside the grid loops, so each resize frame leaked GDI handles until painting stalled
        // and the plot smeared.
        float dpiScale = Math.Max(1f, Theme.DpiScale);
        var plot = PlotRect;
        using var gridPen = new Pen(Theme.Grid, 1f);
        using var axisPen = new Pen(Theme.Axis, 1.2f);
        using var minorFont = new Font("Segoe UI", 8f * dpiScale);
        using var labelFont = new Font("Microsoft YaHei UI", 8.25f * dpiScale, FontStyle.Bold);
        using var hintFont = new Font("Microsoft YaHei UI", 8f * dpiScale);
        using var mutedBrush = new SolidBrush(Theme.Muted);
        using var textBrush = new SolidBrush(Theme.Text);
        using var warningBrush = new SolidBrush(Theme.Warning);

        using (var plotBrush = new SolidBrush(Theme.Surface))
        {
            g.FillRectangle(plotBrush, plot);
        }

        for (int t = MinTemp; t <= MaxTemp; t += 5)
        {
            float x = XAtTemp(t);
            g.DrawLine(t % 20 == 0 ? axisPen : gridPen, x, plot.Y, x, plot.Bottom);
            if (t % 20 == 0)
            {
                string caption = $"{t}°";
                var size = g.MeasureString(caption, minorFont);
                g.DrawString(caption, minorFont, mutedBrush, x - size.Width / 2, plot.Bottom + S(6));
            }
        }

        for (int p = 0; p <= 100; p += 5)
        {
            float y = YAtPct(p);
            g.DrawLine(p % 25 == 0 ? axisPen : gridPen, plot.X, y, plot.Right, y);
            if (p % 25 == 0)
            {
                string caption = $"{p}";
                var size = g.MeasureString(caption, minorFont);
                g.DrawString(caption, minorFont, mutedBrush, plot.X - size.Width - S(9), y - size.Height / 2);
            }
        }

        g.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height);
        g.DrawString("风扇转速 %", labelFont, textBrush, S(8), S(4));
        var axisCaption = g.MeasureString("核心温度 °C", labelFont);
        float captionY = plot.Bottom + S(23);
        g.DrawString("核心温度 °C", labelFont, textBrush, plot.Right - axisCaption.Width, captionY);

        if (_points.Count > 1)
        {
            var curve = new FanCurve(_points);
            var path = new PointF[MaxTemp - MinTemp + 1];
            for (int i = 0; i < path.Length; i++)
            {
                path[i] = new PointF(XAtTemp(MinTemp + i), YAtPct(curve.FanPercentAt(MinTemp + i)));
            }

            var area = path.ToList();
            area.Add(new PointF(path[^1].X, plot.Bottom));
            area.Add(new PointF(path[0].X, plot.Bottom));
            using var fill = new LinearGradientBrush(plot,
                Color.FromArgb(52, Theme.Curve), Color.FromArgb(4, Theme.Curve), 90f);
            g.FillPolygon(fill, area.ToArray());

            using var curvePen = new Pen(Theme.Curve, 2.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(curvePen, path);
        }

        for (int i = 0; i < _points.Count; i++)
        {
            var (t, p) = _points[i];
            float x = XAtTemp(t), y = YAtPct(p);
            bool selected = _selectedIndex == i;
            bool active = _dragIndex == i || _hoverIndex == i || selected;
            float radius = active ? 7f * dpiScale : 6f * dpiScale;
            using var halo = new SolidBrush(selected ? Color.FromArgb(56, Theme.Curve)
                : active ? Color.FromArgb(38, Theme.Curve) : Color.FromArgb(18, Theme.Curve));
            g.FillEllipse(halo, x - radius - S(5), y - radius - S(5), (radius + S(5)) * 2, (radius + S(5)) * 2);
            using var center = new SolidBrush(active ? Theme.Curve : Theme.PlotBackground);
            using var ring = new Pen(Theme.Curve, 2.2f);
            g.FillEllipse(center, x - radius, y - radius, radius * 2, radius * 2);
            g.DrawEllipse(ring, x - radius, y - radius, radius * 2, radius * 2);
            if (selected)
            {
                using var focusPen = new Pen(Theme.Marker, 1.6f) { DashStyle = DashStyle.Dot };
                g.DrawEllipse(focusPen, x - radius - S(5), y - radius - S(5),
                    (radius + S(5)) * 2, (radius + S(5)) * 2);
            }

            string caption = $"{t}°  {p}%";
            var size = g.MeasureString(caption, labelFont);
            float cx = Math.Clamp(x - size.Width / 2, plot.X + S(2), plot.Right - size.Width - S(2));
            float cy = Math.Max(plot.Y + S(2), y - size.Height - S(12));
            using var back = new SolidBrush(Mixed(Theme.Surface, Theme.Text, 0.12f));
            using var backPen = new Pen(Theme.Border);
            g.FillRectangle(back, cx - S(3), cy, size.Width + S(6), size.Height);
            g.DrawRectangle(backPen, cx - S(3), cy, size.Width + S(6), size.Height);
            g.DrawString(caption, labelFont, textBrush, cx, cy);
        }

        if (_currentTemp >= MinTemp && _currentTemp <= MaxTemp)
        {
            float x = XAtTemp(_currentTemp);
            int pct = _points.Count > 0 ? new FanCurve(_points).FanPercentAt(_currentTemp) : 50;
            using var markerPen = new Pen(Theme.Marker, 1.6f) { DashStyle = DashStyle.Dash };
            g.DrawLine(markerPen, x, plot.Y, x, plot.Bottom);
            using var markerBrush = new SolidBrush(Theme.Marker);
            g.FillEllipse(markerBrush, x - 4.5f, YAtPct(pct) - 4.5f, 9, 9);
            string caption = $"当前 {_currentTemp}°C  →  {pct}%";
            var size = g.MeasureString(caption, labelFont);
            float cx = Math.Clamp(x + S(8), plot.X + S(4), plot.Right - size.Width - S(6));
            using var back = new SolidBrush(Mixed(Theme.Surface, Theme.Warning, 0.14f));
            g.FillRectangle(back, cx - S(4), plot.Y + S(6), size.Width + S(8), size.Height + S(2));
            g.DrawString(caption, labelFont, warningBrush, cx, plot.Y + S(7));
        }

        // The hint shares the row under the plot with the axis caption, so it is clipped to the
        // space left of that caption instead of running underneath it at large font scales.
        string hint = "双击空白加点 · 双击圆点删点 · 拖动或方向键微调（Shift 加速，最多 12 点）";
        float hintX = plot.X + S(6);
        float hintWidth = plot.Right - axisCaption.Width - S(10) - hintX;
        if (hintWidth > S(80))
        {
            using var hintFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
            };
            g.DrawString(hint, hintFont, mutedBrush,
                new RectangleF(hintX, plot.Bottom + S(24), hintWidth, hintFont.Height * 2), hintFormat);
        }
    }

    private int? HitTest(Point pos)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            int tolerance = S(11);
            if (Math.Abs(pos.X - XAtTemp(_points[i].TempC)) <= tolerance && Math.Abs(pos.Y - YAtPct(_points[i].Percent)) <= tolerance)
            {
                return i;
            }
        }

        return null;
    }

    private static Color Mixed(Color first, Color second, float secondAmount)
        => Color.FromArgb(
            (int)Math.Round(first.A + (second.A - first.A) * secondAmount),
            (int)Math.Round(first.R + (second.R - first.R) * secondAmount),
            (int)Math.Round(first.G + (second.G - first.G) * secondAmount),
            (int)Math.Round(first.B + (second.B - first.B) * secondAmount));

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragIndex = HitTest(e.Location);
        _selectedIndex = _dragIndex;
        Invalidate();
    }

    /// <summary>Arrow keys nudge the selected point: left/right by one degree, up/down by one
    /// percent, with Shift raising the step to five. The point stays inside its neighbours so the
    /// curve can never fold back on itself, and focus is only taken when a point is selected.</summary>
    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down) return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_selectedIndex is not { } i || i >= _points.Count)
        {
            base.OnKeyDown(e);
            return;
        }

        int step = e.Shift ? 5 : 1;
        var (temp, percent) = _points[i];
        int newTemp = temp;
        int newPercent = percent;

        switch (e.KeyCode)
        {
            case Keys.Left: newTemp = temp - step; break;
            case Keys.Right: newTemp = temp + step; break;
            case Keys.Up: newPercent = percent + step; break;
            case Keys.Down: newPercent = percent - step; break;
            default:
                base.OnKeyDown(e);
                return;
        }

        newTemp = Math.Clamp(newTemp, MinTemp, MaxTemp);
        if (i > 0) newTemp = Math.Max(newTemp, _points[i - 1].TempC + 1);
        if (i < _points.Count - 1) newTemp = Math.Min(newTemp, _points[i + 1].TempC - 1);
        newPercent = Math.Clamp(newPercent, 0, 100);

        if (newTemp == temp && newPercent == percent)
        {
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        _points[i] = (newTemp, newPercent);
        e.Handled = e.SuppressKeyPress = true;
        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragIndex is not { } i)
        {
            int? hover = HitTest(e.Location);
            if (hover != _hoverIndex) { _hoverIndex = hover; Invalidate(); }
            return;
        }

        int newTemp = TempAtX(e.X);
        if (i > 0) newTemp = Math.Max(newTemp, _points[i - 1].TempC + 1);
        if (i < _points.Count - 1) newTemp = Math.Min(newTemp, _points[i + 1].TempC - 1);
        _points[i] = (newTemp, PctAtY(e.Y));
        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !PlotRect.Contains(e.Location)) return;
        if (HitTest(e.Location) is { } idx)
        {
            if (_points.Count > 2)
            {
                _points.RemoveAt(idx);
                _selectedIndex = null;
            }
        }
        else if (_points.Count < MaxPoints)
        {
            _points.Add((TempAtX(e.X), PctAtY(e.Y)));
            _points.Sort((a, b) => a.TempC.CompareTo(b.TempC));
            _selectedIndex = null;
        }

        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }
}
