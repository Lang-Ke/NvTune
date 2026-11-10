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
    private int _currentTemp = -1;

    public FanCurveEditor()
    {
        DoubleBuffered = true;
        BackColor = Theme.PlotBackground;
        ForeColor = Theme.Text;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => { _dragIndex = null; Invalidate(); };
        MouseDoubleClick += OnMouseDoubleClick;
        MouseLeave += (_, _) => { _hoverIndex = null; Invalidate(); };
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
        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefault() => SetPoints(FanCurve.Default.Points);

    private RectangleF PlotRect => new(Math.Max(52, Width * 0.07f), 26,
        Math.Max(60, Width - Math.Max(88, Width * 0.12f)), Math.Max(60, Height - 64));

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

        using var gridPen = new Pen(Theme.Grid, 1f);
        using var axisPen = new Pen(Theme.Axis, 1.2f);
        using var minorFont = new Font("Segoe UI", 8f);
        using var labelFont = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Bold);
        using var hintFont = new Font("Microsoft YaHei UI", 8f);

        using (var plotBrush = new SolidBrush(Theme.Surface))
        {
            g.FillRectangle(plotBrush, PlotRect);
        }

        for (int t = MinTemp; t <= MaxTemp; t += 5)
        {
            float x = XAtTemp(t);
            g.DrawLine(t % 20 == 0 ? axisPen : gridPen, x, PlotRect.Y, x, PlotRect.Bottom);
            if (t % 20 == 0)
            {
                string caption = $"{t}°";
                var size = g.MeasureString(caption, minorFont);
                g.DrawString(caption, minorFont, new SolidBrush(Theme.Muted), x - size.Width / 2, PlotRect.Bottom + 6);
            }
        }

        for (int p = 0; p <= 100; p += 5)
        {
            float y = YAtPct(p);
            g.DrawLine(p % 25 == 0 ? axisPen : gridPen, PlotRect.X, y, PlotRect.Right, y);
            if (p % 25 == 0)
            {
                string caption = $"{p}";
                var size = g.MeasureString(caption, minorFont);
                g.DrawString(caption, minorFont, new SolidBrush(Theme.Muted), PlotRect.X - size.Width - 9, y - size.Height / 2);
            }
        }

        g.DrawRectangle(axisPen, PlotRect.X, PlotRect.Y, PlotRect.Width, PlotRect.Height);
        g.DrawString("风扇转速 %", labelFont, new SolidBrush(Theme.Text), 8, 4);
        g.DrawString("核心温度 °C", labelFont, new SolidBrush(Theme.Text), PlotRect.Right - 76, PlotRect.Bottom + 23);

        if (_points.Count > 1)
        {
            var curve = new FanCurve(_points);
            var path = new PointF[MaxTemp - MinTemp + 1];
            for (int i = 0; i < path.Length; i++)
            {
                path[i] = new PointF(XAtTemp(MinTemp + i), YAtPct(curve.FanPercentAt(MinTemp + i)));
            }

            var area = path.ToList();
            area.Add(new PointF(path[^1].X, PlotRect.Bottom));
            area.Add(new PointF(path[0].X, PlotRect.Bottom));
            using var fill = new LinearGradientBrush(PlotRect,
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
            bool active = _dragIndex == i || _hoverIndex == i;
            float radius = active ? 7f : 6f;
            using var halo = new SolidBrush(active ? Color.FromArgb(38, Theme.Curve) : Color.FromArgb(18, Theme.Curve));
            g.FillEllipse(halo, x - radius - 5, y - radius - 5, (radius + 5) * 2, (radius + 5) * 2);
            using var center = new SolidBrush(active ? Theme.Curve : Theme.PlotBackground);
            using var ring = new Pen(Theme.Curve, 2.2f);
            g.FillEllipse(center, x - radius, y - radius, radius * 2, radius * 2);
            g.DrawEllipse(ring, x - radius, y - radius, radius * 2, radius * 2);

            string caption = $"{t}°  {p}%";
            var size = g.MeasureString(caption, labelFont);
            float cx = Math.Clamp(x - size.Width / 2, PlotRect.X + 2, PlotRect.Right - size.Width - 2);
            float cy = Math.Max(PlotRect.Y + 2, y - size.Height - 12);
            using var back = new SolidBrush(Mixed(Theme.Surface, Theme.Text, 0.12f));
            using var backPen = new Pen(Theme.Border);
            g.FillRectangle(back, cx - 3, cy, size.Width + 6, size.Height);
            g.DrawRectangle(backPen, cx - 3, cy, size.Width + 6, size.Height);
            g.DrawString(caption, labelFont, new SolidBrush(Theme.Text), cx, cy);
        }

        if (_currentTemp >= MinTemp && _currentTemp <= MaxTemp)
        {
            float x = XAtTemp(_currentTemp);
            int pct = _points.Count > 0 ? new FanCurve(_points).FanPercentAt(_currentTemp) : 50;
            using var markerPen = new Pen(Theme.Marker, 1.6f) { DashStyle = DashStyle.Dash };
            g.DrawLine(markerPen, x, PlotRect.Y, x, PlotRect.Bottom);
            using var markerBrush = new SolidBrush(Theme.Marker);
            g.FillEllipse(markerBrush, x - 4.5f, YAtPct(pct) - 4.5f, 9, 9);
            string caption = $"当前 {_currentTemp}°C  →  {pct}%";
            var size = g.MeasureString(caption, labelFont);
            float cx = Math.Clamp(x + 8, PlotRect.X + 4, PlotRect.Right - size.Width - 6);
            using var back = new SolidBrush(Mixed(Theme.Surface, Theme.Warning, 0.14f));
            g.FillRectangle(back, cx - 4, PlotRect.Y + 6, size.Width + 8, size.Height + 2);
            g.DrawString(caption, labelFont, new SolidBrush(Theme.Warning), cx, PlotRect.Y + 7);
        }

        g.DrawString("双击空白加点 · 双击圆点删点 · 拖动圆点调整（最多 12 点）",
            hintFont, new SolidBrush(Theme.Muted), PlotRect.X + 6, PlotRect.Bottom + 24);
    }

    private int? HitTest(Point pos)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            if (Math.Abs(pos.X - XAtTemp(_points[i].TempC)) <= 11 && Math.Abs(pos.Y - YAtPct(_points[i].Percent)) <= 11)
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
        _dragIndex = HitTest(e.Location);
        Invalidate();
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
            if (_points.Count > 2) _points.RemoveAt(idx);
        }
        else if (_points.Count < MaxPoints)
        {
            _points.Add((TempAtX(e.X), PctAtY(e.Y)));
            _points.Sort((a, b) => a.TempC.CompareTo(b.TempC));
        }

        Invalidate();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }
}
