using System.Drawing.Drawing2D;

namespace SimpleNvidiaUndervolt;

internal sealed class MetricTile(string caption, string unit, Color accent) : Control
{
    protected override void OnCreateControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        base.OnCreateControl();
    }
    private string _value = "—";
    private string _unit = unit;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Unit { get => _unit; set { if (_unit != value) { _unit = value; Invalidate(); } } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value { get => _value; set { if (_value != value) { _value = value; Invalidate(); } } }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        var g = e.Graphics;
        g.Clear(Theme.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Pixels(this, 10));
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border);
        g.FillPath(fill, path); g.DrawPath(border, path);
        int inset = Theme.Pixels(this, 16);
        using var labelFont = Theme.DrawingFont(this, 8.5f);
        using var numberFont = Theme.DrawingFont(this, 20, FontStyle.Bold, "Segoe UI");
        using var unitFont = Theme.DrawingFont(this, 9, FontStyle.Regular, "Segoe UI");
        using var muted = new SolidBrush(Theme.Muted);
        using var foreground = new SolidBrush(Theme.Text);
        using var dot = new SolidBrush(accent);
        g.DrawString(caption, labelFont, muted, inset, Theme.Pixels(this, 8));
        g.FillEllipse(dot, Width - inset - Theme.Pixels(this, 5), Theme.Pixels(this, 12), Theme.Pixels(this, 5), Theme.Pixels(this, 5));
        g.DrawString(Value, numberFont, foreground, inset - Theme.Pixels(this, 2), Theme.Pixels(this, 28));
        var size = g.MeasureString(Value, numberFont);
        g.DrawString(Unit, unitFont, muted, Math.Min(inset + size.Width, Width - inset - g.MeasureString(Unit, unitFont).Width), Theme.Pixels(this, 44));
    }
}
