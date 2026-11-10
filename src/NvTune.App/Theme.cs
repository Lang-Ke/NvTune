using System.Drawing.Drawing2D;

namespace SimpleNvidiaUndervolt;

/// <summary>One place for the palette and the shared control styling, so the form and the curve
/// editor cannot drift apart. The window runs in a single light palette: a live dark switch kept
/// leaving native child controls half-painted, so the dark option was removed outright.</summary>
internal static class Theme
{
    private sealed record Palette(
        Color Canvas, Color Surface, Color PlotBackground, Color Border, Color Text, Color Muted,
        Color Accent, Color AccentHover, Color AccentDown, Color AccentSoft,
        Color Neutral, Color NeutralHover,
        Color Success, Color SuccessSoft, Color Warning, Color WarningSoft,
        Color Danger, Color DangerSoft,
        Color Grid, Color Axis, Color Curve, Color Marker,
        Color AccentAlt, Color CanvasAlt, Color Shadow);

    private static readonly Palette LightPalette = new(
        Canvas: Color.FromArgb(244, 246, 252),
        Surface: Color.White,
        PlotBackground: Color.FromArgb(249, 250, 255),
        Border: Color.FromArgb(226, 230, 242),
        Text: Color.FromArgb(30, 36, 56),
        Muted: Color.FromArgb(113, 122, 145),
        Accent: Color.FromArgb(99, 102, 241),
        AccentHover: Color.FromArgb(79, 82, 226),
        AccentDown: Color.FromArgb(67, 70, 199),
        AccentSoft: Color.FromArgb(238, 240, 255),
        Neutral: Color.FromArgb(242, 244, 250),
        NeutralHover: Color.FromArgb(232, 235, 245),
        Success: Color.FromArgb(16, 138, 108),
        SuccessSoft: Color.FromArgb(232, 250, 244),
        Warning: Color.FromArgb(190, 96, 12),
        WarningSoft: Color.FromArgb(255, 246, 235),
        Danger: Color.FromArgb(220, 62, 78),
        DangerSoft: Color.FromArgb(254, 240, 242),
        Grid: Color.FromArgb(233, 236, 246),
        Axis: Color.FromArgb(198, 206, 224),
        Curve: Color.FromArgb(99, 102, 241),
        Marker: Color.FromArgb(240, 150, 20),
        AccentAlt: Color.FromArgb(14, 165, 233),
        CanvasAlt: Color.FromArgb(238, 241, 252),
        Shadow: Color.FromArgb(18, 30, 70, 120));

    private static readonly Palette _palette = LightPalette;

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

    public static Font TitleFont(float size = 16f) => new("Microsoft YaHei UI", size, FontStyle.Bold);

    public static Color Mix(Color first, Color second, float secondAmount)
    {
        float t = Math.Clamp(secondAmount, 0f, 1f);
        return Color.FromArgb(
            (int)(first.R + (second.R - first.R) * t),
            (int)(first.G + (second.G - first.G) * t),
            (int)(first.B + (second.B - first.B) * t));
    }
    public static Font CardFont() => new("Microsoft YaHei UI", 11f, FontStyle.Bold);
    public static Font BodyFont() => new("Microsoft YaHei UI", 9.5f);
    public static Font SmallFont() => new("Microsoft YaHei UI", 8.5f);

    public static Button PrimaryButton(string text, EventHandler onClick, int width = 128)
    {
        var button = new ModernButton
        {
            Kind = ButtonKind.Primary,
            Text = text,
            BackColor = Canvas,
            ForeColor = Text,
            Width = width,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
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
            Width = width,
            Font = new Font("Microsoft YaHei UI", 9.5f),
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
            Width = width,
            Height = 32,
            Font = BodyFont(),
            Margin = new Padding(0, 0, 0, 0),
        };
        return input;
    }

    public static ComboBox Combo(int width, bool dropDownList = true)
    {
        return new DarkCombo
        {
            DropDownStyle = dropDownList ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown,
            Width = width,
            Height = 32,
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

    /// <summary>A rounded card with a hairline border and an accent bar beside its title. The card
    /// paints its own background so the corners stay round; its children sit inside the padding.</summary>
    public static Panel Card(string title, out Panel body)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(18, 12, 18, 14),
            MinimumSize = new Size(0, 124),
        };
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // A soft drop shadow under the card reads as depth without a heavy border.
            var shadow = new Rectangle(2, 3, card.Width - 5, card.Height - 5);
            using (GraphicsPath shadowPath = Rounded(shadow, 14))
            using (var shadowBrush = new SolidBrush(Shadow)) e.Graphics.FillPath(shadowBrush, shadowPath);

            var rect = new Rectangle(0, 0, card.Width - 2, card.Height - 4);
            using GraphicsPath path = Rounded(rect, 12);
            using (var fill = new SolidBrush(Surface)) e.Graphics.FillPath(fill, path);
            using var pen = new Pen(Border);
            e.Graphics.DrawPath(pen, path);
        };

        var heading = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Surface };
        var bar = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Surface, Margin = new Padding(0, 8, 0, 8) };
        bar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var barRect = new Rectangle(0, 4, 4, Math.Max(6, bar.Height - 8));
            using GraphicsPath barPath = Rounded(barRect, 2);
            using var gradient = new LinearGradientBrush(barRect, Accent, AccentAlt, 90f);
            e.Graphics.FillPath(gradient, barPath);
        };
        heading.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Text,
            Font = CardFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        });
        heading.Controls.Add(bar);
        body = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(0, 4, 0, 0) };
        card.Controls.Add(body);
        card.Controls.Add(heading);
        return card;
    }

    internal static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum ButtonKind { Primary, Secondary }

/// <summary>A numeric field built from a borderless text box and painted arrows. The stock
/// NumericUpDown locks its height to the font metrics and paints its editor with a system brush
/// that ignores BackColor, so it can be neither 32px tall nor reliably dark.</summary>
internal sealed class DarkNumeric : Control
{
    private const int SpinnerWidth = 18;
    private readonly TextBox _editor;
    private decimal _value;
    private decimal _minimum;
    private decimal _maximum = 100;
    private bool _updating;

    public event EventHandler? ValueChanged;

    public DarkNumeric()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;
        BackColor = Theme.Neutral;
        ForeColor = Theme.Text;
        Cursor = Cursors.Default;
        _editor = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Neutral,
            ForeColor = Theme.Text,
            TextAlign = HorizontalAlignment.Left,
        };
        _editor.GotFocus += (_, _) => Invalidate();
        _editor.LostFocus += (_, _) => { CommitText(); Invalidate(); };
        _editor.KeyDown += OnEditorKeyDown;
        _editor.MouseWheel += OnEditorWheel;
        Controls.Add(_editor);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            // Initialisers may set Minimum before Maximum; only clamp once the range is valid.
            if (_minimum <= _maximum && _value < _minimum) Value = _minimum;
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum <= _maximum && _value > _maximum) Value = _maximum;
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => _value;
        set
        {
            decimal clamped = _minimum <= _maximum ? Math.Clamp(value, _minimum, _maximum) : value;
            bool changed = clamped != _value;
            _value = clamped;
            ShowValue();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public decimal Increment { get; set; } = 1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public HorizontalAlignment TextAlign
    {
        get => _editor.TextAlign;
        set => _editor.TextAlign = value;
    }

    /// <summary>Re-tints the hosted text box after a live palette switch. The surrounding field is
    /// painted from the palette, but a TextBox keeps whatever colours it was constructed with.</summary>
    public void RefreshPalette()
    {
        BackColor = Theme.Neutral;
        _editor.BackColor = Theme.Neutral;
        _editor.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        Invalidate();
    }

    private void ShowValue()
    {
        _updating = true;
        _editor.Text = _value.ToString("0");
        _updating = false;
    }

    private void CommitText()
    {
        if (_updating) return;
        if (!decimal.TryParse(_editor.Text.Trim(), out decimal parsed))
        {
            ShowValue();
            return;
        }
        decimal clamped = Math.Clamp(parsed, _minimum, _maximum);
        bool changed = clamped != _value;
        _value = clamped;
        ShowValue();
        if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Step(int direction)
    {
        Value = _value + Increment * direction;
        Invalidate();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { CommitText(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Up) { Step(1); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Down) { Step(-1); e.SuppressKeyPress = true; }
    }

    private void OnEditorWheel(object? sender, MouseEventArgs e)
    {
        if (!_editor.Focused) return;
        Step(e.Delta > 0 ? 1 : -1);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_editor is null) return;
        int editorHeight = Math.Min(_editor.PreferredHeight, Math.Max(10, Height - 6));
        _editor.Bounds = new Rectangle(10, Math.Max(1, (Height - editorHeight) / 2),
            Math.Max(8, Width - SpinnerWidth - 16), editorHeight);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _editor.Font = Font;
        PerformLayout();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _editor.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var clear = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(clear, ClientRectangle);

        var field = new Rectangle(0, 0, Math.Max(10, Width - 1), Height - 1);
        using (GraphicsPath path = Theme.Rounded(field, 7))
        {
            using (var fill = new SolidBrush(Theme.Neutral)) g.FillPath(fill, path);
            using var pen = new Pen(ContainsFocus ? Theme.Accent : Theme.Border);
            g.DrawPath(pen, path);
        }

        var buttons = new Rectangle(Width - SpinnerWidth, 1, SpinnerWidth - 2, Height - 3);
        using (var divider = new Pen(Theme.Border)) g.DrawLine(divider, buttons.Left, 5, buttons.Left, Height - 6);
        DrawArrow(g, new Rectangle(buttons.Left, buttons.Top, buttons.Width, buttons.Height / 2), true);
        DrawArrow(g, new Rectangle(buttons.Left, buttons.Top + buttons.Height / 2, buttons.Width, buttons.Height / 2), false);
    }

    private static void DrawArrow(Graphics g, Rectangle cell, bool up)
    {
        int cx = cell.Left + cell.Width / 2, cy = cell.Top + cell.Height / 2;
        Point[] points = up
            ? new[] { new Point(cx - 4, cy + 2), new Point(cx + 4, cy + 2), new Point(cx, cy - 3) }
            : new[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
        using var brush = new SolidBrush(Theme.Muted);
        g.FillPolygon(brush, points);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.X >= Width - SpinnerWidth)
        {
            Step(e.Y < Height / 2 ? 1 : -1);
            Focus();
            Invalidate();
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _editor.Focus(); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (Focused) Step(e.Delta > 0 ? 1 : -1); }
}

/// <summary>A combo box that paints its own dark field and chevron. The stock drop-down paints a
/// light client strip that no BackColor reaches in dark mode.</summary>
internal sealed class DarkCombo : ComboBox
{
    public DarkCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        // ComboBox height is derived from ItemHeight, so this is what makes the field 32px and
        // keeps it flush with the numeric boxes and buttons beside it.
        ItemHeight = 26;
        DropDownStyle = ComboBoxStyle.DropDownList;
        IntegralHeight = false;
        DropDownHeight = 240;
        BackColor = Theme.Neutral;
        ForeColor = Theme.Text;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color back = selected ? Theme.AccentSoft : Theme.Neutral;
        Color fore = selected ? Theme.Text : Theme.Muted;
        using (var fill = new SolidBrush(back)) e.Graphics.FillRectangle(fill, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? string.Empty, Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
            fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
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
        Height = 36;
        Cursor = Cursors.Default;
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
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var clear = new SolidBrush(Parent?.BackColor ?? Theme.Canvas)) g.FillRectangle(clear, ClientRectangle);

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
            fore = Color.White;
        }
        else
        {
            solidBack = _down ? Theme.Border : _hover ? Theme.NeutralHover : Theme.Neutral;
            fore = Theme.Text;
        }

        using GraphicsPath path = Theme.Rounded(rect, 8);
        if (gradient is not null)
        {
            using (gradient) g.FillPath(gradient, path);
        }
        else if (solidBack is { } back)
        {
            using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
        }

        if (Kind == ButtonKind.Secondary)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
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
