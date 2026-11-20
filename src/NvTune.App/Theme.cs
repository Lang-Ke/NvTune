using System.Drawing.Drawing2D;

namespace SimpleNvidiaUndervolt;

/// <summary>One place for the palette and the shared control styling, so the form and the curve
/// editor cannot drift apart. Layout is authored at 96 DPI and scaled by WinForms.</summary>
internal static class Theme
{
    // Layout is authored at 96 DPI; WinForms owns production scaling.
    internal static float? DpiScaleOverride { get; set; }
    public static int Scaled(int value) => (int)Math.Round(value * (DpiScaleOverride ?? 1));
    public static float ScaleFor(Control control) => DpiScaleOverride ?? control.DeviceDpi / 96f;
    public static int Pixels(Control control, int value) => (int)Math.Round(value * ScaleFor(control));
    public static Font DrawingFont(Control owner, float points, FontStyle style = FontStyle.Regular, string family = "Microsoft YaHei UI")
        => new(family, points * (96f / 72f) * ScaleFor(owner), style, GraphicsUnit.Pixel);
    private sealed record Palette(
        Color Canvas, Color Surface, Color PlotBackground, Color Border, Color Text, Color Muted,
        Color Accent, Color AccentHover, Color AccentDown, Color AccentSoft,
        Color Neutral, Color NeutralHover,
        Color Success, Color SuccessSoft, Color Warning, Color WarningSoft,
        Color Danger, Color DangerSoft,
        Color Grid, Color Axis, Color Curve, Color Marker,
        Color AccentAlt, Color CanvasAlt, Color Shadow);

    private static readonly Palette _palette = new(
        Canvas: Color.FromArgb(10, 15, 23),
        Surface: Color.FromArgb(18, 27, 40),
        PlotBackground: Color.FromArgb(13, 21, 32),
        Border: Color.FromArgb(39, 54, 73),
        Text: Color.FromArgb(232, 239, 248),
        Muted: Color.FromArgb(152, 169, 192),
        Accent: Color.FromArgb(70, 218, 190),
        AccentHover: Color.FromArgb(108, 235, 210),
        AccentDown: Color.FromArgb(41, 180, 157),
        AccentSoft: Color.FromArgb(23, 58, 57),
        Neutral: Color.FromArgb(12, 20, 31),
        NeutralHover: Color.FromArgb(29, 43, 61),
        Success: Color.FromArgb(70, 218, 190),
        SuccessSoft: Color.FromArgb(23, 58, 57),
        Warning: Color.FromArgb(248, 191, 97),
        WarningSoft: Color.FromArgb(65, 48, 27),
        Danger: Color.FromArgb(255, 137, 151),
        DangerSoft: Color.FromArgb(62, 30, 42),
        Grid: Color.FromArgb(28, 40, 56),
        Axis: Color.FromArgb(64, 81, 104),
        Curve: Color.FromArgb(70, 218, 190),
        Marker: Color.FromArgb(248, 191, 97),
        AccentAlt: Color.FromArgb(100, 167, 255),
        CanvasAlt: Color.FromArgb(15, 22, 33),
        Shadow: Color.FromArgb(20, 0, 0, 0));
    public static Color Canvas => _palette.Canvas;
    public static Color Surface => _palette.Surface;
    public static Color PlotBackground => _palette.PlotBackground;
    public static Color Border => _palette.Border;
    public static Color Text => _palette.Text;
    public static Color Muted => _palette.Muted;
    public static Color Accent => _palette.Accent;
    public static Color AccentHover => _palette.AccentHover;
    public static Color AccentDown => _palette.AccentDown;
    public static Color AccentSoft => _palette.AccentSoft;
    public static Color Neutral => _palette.Neutral;
    public static Color NeutralHover => _palette.NeutralHover;
    public static Color Success => _palette.Success;
    public static Color SuccessSoft => _palette.SuccessSoft;
    public static Color Warning => _palette.Warning;
    public static Color WarningSoft => _palette.WarningSoft;
    public static Color Danger => _palette.Danger;
    public static Color DangerSoft => _palette.DangerSoft;
    public static Color Grid => _palette.Grid;
    public static Color Axis => _palette.Axis;
    public static Color Curve => _palette.Curve;
    public static Color Marker => _palette.Marker;
    public static Color AccentAlt => _palette.AccentAlt;
    public static Color CanvasAlt => _palette.CanvasAlt;
    public static Color Shadow => _palette.Shadow;

    /// <summary>Point sizes already scale with the device DPI, so production passes them through.
    /// Only the simulated-DPI test hook inflates them, keeping the harness faithful.</summary>
    private static float Pt(float points) => DpiScaleOverride is { } forced && forced > 0 ? points * forced : points;

    public static Font TitleFont(float size = 16f) => new("Microsoft YaHei UI", Pt(size), FontStyle.Bold);

    public static Color Mix(Color first, Color second, float secondAmount)
    {
        float t = Math.Clamp(secondAmount, 0f, 1f);
        return Color.FromArgb(
            (int)(first.R + (second.R - first.R) * t),
            (int)(first.G + (second.G - first.G) * t),
            (int)(first.B + (second.B - first.B) * t));
    }
    public static Font CardFont() => new("Microsoft YaHei UI", Pt(11f), FontStyle.Bold);
    public static Font BodyFont() => new("Microsoft YaHei UI", Pt(9.5f));
    public static Font SmallFont() => new("Microsoft YaHei UI", Pt(8.5f));
    public static Font SmallBoldFont() => new("Microsoft YaHei UI", Pt(8.5f), FontStyle.Bold);

    public static Button PrimaryButton(string text, EventHandler onClick, int width = 128)
    {
        var button = new ModernButton
        {
            Kind = ButtonKind.Primary,
            Text = text,
            BackColor = Canvas,
            ForeColor = Text,
            Width = Scaled(width),
            Font = new Font("Microsoft YaHei UI", Pt(9.5f), FontStyle.Bold),
        };
        button.Click += onClick;
        return button;
    }

    public static Button SecondaryButton(string text, EventHandler onClick, int width = 112)
    {
        var button = new ModernButton
        {
            Kind = ButtonKind.Secondary,
            Text = text,
            Width = Scaled(width),
            Font = new Font("Microsoft YaHei UI", Pt(9.5f)),
        };
        button.Click += onClick;
        return button;
    }

    public static DarkNumeric Numeric(decimal minimum, decimal maximum, decimal value, int width = 96)
    {
        var input = new DarkNumeric
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = Scaled(width),
            Height = Scaled(36),
            Font = BodyFont(),
            Margin = new Padding(0, 0, 0, 0),
        };
        return input;
    }

    public static DarkCombo Combo(int width)
    {
        return new DarkCombo
        {
            Width = Scaled(width),
            Height = Scaled(36),
            Font = BodyFont(),
            Margin = new Padding(0, 0, 0, 0),
        };
    }

    public static Label Caption(string text)
        => new() { Text = text, AutoSize = true, ForeColor = Muted, Font = SmallFont(), Margin = new Padding(0, 0, 0, 2) };

    /// <summary>A table used for every row of controls: columns size themselves, rows line up, and
    /// no child can push a sibling out of alignment the way a flow panel does.</summary>
    public static TableLayoutPanel Table(int columns, int rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            BackColor = Surface,
            AutoSize = false,
        };
        for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return grid;
    }

    /// <summary>Wraps a control in a middle-left aligned cell so inputs and buttons share one
    /// vertical centre line whatever their intrinsic height.</summary>
    public static Control Cell(Control control, Padding margin)
    {
        control.Anchor = AnchorStyles.Left;
        control.Margin = margin;
        return control;
    }

    public static Label CellLabel(string text, Padding margin)
    {
        Label label = new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Muted,
            Font = SmallFont(),
            Margin = margin,
        };
        return label;
    }

    public static Label Body(string text)
        => new() { Text = text, AutoSize = true, ForeColor = Text, Font = BodyFont() };

    /// <summary>A flat card with a hairline border and an accent bar beside its title. Children
    /// sit inside the padding; the whole window uses the canvas colour so no rounding artefacts.</summary>
    public static Panel Card(string title, out Panel body)
    {
        var card = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Canvas,
            Margin = new Padding(0, 0, 0, Scaled(10)),
            Padding = new Padding(Scaled(16), Scaled(10), Scaled(16), Scaled(12)),
            MinimumSize = new Size(0, Scaled(124)),
        };
        card.Paint += (_, e) =>
        {
            // A resize can hand the panel a near-zero rectangle.
            if (card.Width < 12 || card.Height < 12) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var outline = Rounded(rect, Pixels(card, 12));
            using (var fill = new SolidBrush(Surface)) e.Graphics.FillPath(fill, outline);
            using var pen = new Pen(Border);
            e.Graphics.DrawPath(pen, outline);
        };

        var heading = new BufferedPanel { Dock = DockStyle.Top, Height = Scaled(32), BackColor = Surface };
        var bar = new BufferedPanel { Dock = DockStyle.Left, Width = Scaled(4), BackColor = Surface };
        bar.Paint += (_, e) =>
        {
            if (bar.Height < Pixels(bar, 14)) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var barRect = new Rectangle(0, Pixels(bar, 8), Pixels(bar, 3), Math.Max(Pixels(bar, 6), bar.Height - Pixels(bar, 16)));
            using var gradient = new LinearGradientBrush(barRect, Accent, AccentAlt, 90f);
            e.Graphics.FillRectangle(gradient, barRect);
        };
        heading.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Text,
            Font = CardFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Scaled(12), 0, 0, 0),
        });
        heading.Controls.Add(bar);
        body = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(0, Scaled(4), 0, 0) };
        card.Controls.Add(body);
        card.Controls.Add(heading);
        return card;
    }

    internal static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum ButtonKind { Primary, Secondary }

/// <summary>A Panel with painting double-buffered, so custom-drawn cards don't leave fragments
/// behind while the window is being resized.</summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
    }
}

/// <summary>A flat button that paints itself: rounded corners, a hover wash and a focus cue. The
/// palette is read at paint time, so a live theme switch only needs a repaint.</summary>
internal sealed class ModernButton : Button
{
    private bool _hover;
    private bool _down;

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = Theme.Scaled(36);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        if (Width < 8 || Height < 8) return;
        // Clear without antialiasing so old rectangular edges cannot survive in the corners.
        g.Clear(Parent?.BackColor ?? Theme.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fore;
        LinearGradientBrush? gradient = null;
        Color? solidBack = null;
        if (!Enabled)
        {
            solidBack = Theme.Neutral;
            fore = Theme.Muted;
        }
        else if (Kind == ButtonKind.Primary)
        {
            // Aurora-style accent: a gentle indigo to cyan sweep, brightened on hover.
            Color start = _down ? Theme.AccentDown : _hover ? Theme.AccentHover : Theme.Accent;
            Color end = _down ? Theme.Accent : _hover ? Theme.AccentAlt : Theme.Mix(Theme.AccentAlt, Theme.Accent, 0.35f);
            gradient = new LinearGradientBrush(Rectangle.Inflate(rect, 2, 2), start, end, 0f);
            fore = Theme.Canvas;
        }
        else
        {
            solidBack = _down ? Theme.Border : _hover ? Theme.NeutralHover : Theme.Neutral;
            fore = ForeColor == Theme.Danger ? Theme.Danger : Theme.Text;
        }

        using var outline = Theme.Rounded(rect, Theme.Pixels(this, 7));
        if (gradient is not null)
        {
            using (gradient) g.FillPath(gradient, outline);
        }
        else if (solidBack is { } back)
        {
            using (var fill = new SolidBrush(back)) g.FillPath(fill, outline);
        }

        if (Kind == ButtonKind.Secondary)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, outline);
        }

        TextRenderer.DrawText(g, Text, Font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -4, -4), fore, solidBack ?? Theme.Accent);
        }
    }
}
