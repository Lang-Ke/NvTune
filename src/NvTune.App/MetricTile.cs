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
    private string _note = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Unit { get => _unit; set { if (_unit != value) { _unit = value; Invalidate(); } } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value { get => _value; set { if (_value != value) { _value = value; Invalidate(); } } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Note { get => _note; set { if (_note != value) { _note = value; Invalidate(); } } }
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
        int inset = Theme.Pixels(this, 12);
        using var labelFont = Theme.DrawingFont(this, 8.5f);
        using var unitFont = Theme.DrawingFont(this, 9, FontStyle.Regular, "Segoe UI");
        using var noteFont = Theme.DrawingFont(this, 8);
        using var muted = new SolidBrush(Theme.Muted);
        using var dot = new SolidBrush(accent);
        g.DrawString(caption, labelFont, muted, inset, Theme.Pixels(this, 8));
        g.FillEllipse(dot, Width - inset - Theme.Pixels(this, 5), Theme.Pixels(this, 12), Theme.Pixels(this, 5), Theme.Pixels(this, 5));
        const TextFormatFlags textFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int contentWidth = Math.Max(1, Width - inset * 2);
        int gap = Unit.Length == 0 ? 0 : Theme.Pixels(this, 4);
        var unitSize = TextRenderer.MeasureText(Unit, unitFont, Size.Empty, textFlags);
        float points = Note.Length == 0 ? 20 : 16;
        var numberFont = Theme.DrawingFont(this, points, FontStyle.Bold, "Segoe UI");
        try
        {
            var numberSize = TextRenderer.MeasureText(Value, numberFont, Size.Empty, textFlags);
            while (numberSize.Width + unitSize.Width + gap > contentWidth && points > 12)
            {
                numberFont.Dispose();
                numberFont = Theme.DrawingFont(this, --points, FontStyle.Bold, "Segoe UI");
                numberSize = TextRenderer.MeasureText(Value, numberFont, Size.Empty, textFlags);
            }
            int numberY = Theme.Pixels(this, Note.Length == 0 ? 27 : 22);
            int numberWidth = Math.Max(1, contentWidth - unitSize.Width - gap);
            TextRenderer.DrawText(g, Value, numberFont,
                new Rectangle(inset, numberY, numberWidth, numberSize.Height), Theme.Text,
                textFlags | TextFormatFlags.EndEllipsis);
            if (Unit.Length > 0)
                TextRenderer.DrawText(g, Unit, unitFont,
                    new Point(inset + Math.Min(numberSize.Width, numberWidth) + gap,
                        numberY + Math.Max(0, numberSize.Height - unitSize.Height - Theme.Pixels(this, 2))),
                    Theme.Muted, textFlags);
        }
        finally { numberFont.Dispose(); }
        if (Note.Length > 0)
            TextRenderer.DrawText(g, Note, noteFont,
                new Rectangle(inset, Theme.Pixels(this, 49), contentWidth, Math.Max(1, Height - Theme.Pixels(this, 53))),
                Theme.Muted, textFlags | TextFormatFlags.EndEllipsis);
    }
}
