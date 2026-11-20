using System.Drawing.Drawing2D;
namespace SimpleNvidiaUndervolt;
internal sealed class DarkNumeric : Control
{
    /// <summary>The arrow strip must grow with the field, otherwise the 18px strip stays tiny
    /// beside a DPI-scaled 56px box and the arrows become hard to hit.</summary>
    private int SpinnerWidth => S(24);
    private int S(int px) => Theme.Pixels(this, px);
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
        Height = Theme.Scaled(36);
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

    /// <summary>True only while a spinner arrow or the wheel is driving the value, so a listener
    /// that snaps values can tell a deliberate step from a typed entry.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Stepping { get; private set; }

    /// <summary>+1 for an up step, -1 for a down step, 0 when the change did not come from the arrows.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int StepDirection { get; private set; }

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
        string text = _editor.Text.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        if (!decimal.TryParse(text, out decimal parsed))
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
        // The editor may hold a value the user typed but never confirmed. Committing first makes
        // the arrow step from what is on screen instead of the previously applied number.
        CommitText();
        StepDirection = direction;
        Stepping = true;
        try
        {
            Value = _value + Increment * direction;
        }
        finally
        {
            Stepping = false;
            StepDirection = 0;
        }
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
        int inset = S(10);
        int editorHeight = Math.Min(_editor.PreferredHeight, Math.Max(10, Height - S(6)));
        _editor.Bounds = new Rectangle(inset, Math.Max(1, (Height - editorHeight) / 2),
            Math.Max(8, Width - SpinnerWidth - inset - S(6)), editorHeight);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_editor is null) return;
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
        if (Width < 12 || Height < 12) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var clear = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(clear, ClientRectangle);

        var field = new Rectangle(0, 0, Math.Max(10, Width - 1), Height - 1);
        using var outline = Theme.Rounded(field, S(7));
        using (var fill = new SolidBrush(Theme.Neutral)) g.FillPath(fill, outline);
        using var pen = new Pen(ContainsFocus ? Theme.Accent : Theme.Border);
        g.DrawPath(pen, outline);

        var buttons = new Rectangle(Width - SpinnerWidth, 1, SpinnerWidth - 2, Height - 3);
        using (var divider = new Pen(Theme.Border))
        {
            g.DrawLine(divider, buttons.Left, S(5), buttons.Left, Height - S(6));
        }
        DrawArrow(g, new Rectangle(buttons.Left, buttons.Top, buttons.Width, buttons.Height / 2), true);
        DrawArrow(g, new Rectangle(buttons.Left, buttons.Top + buttons.Height / 2, buttons.Width, buttons.Height / 2), false);
    }

    private void DrawArrow(Graphics g, Rectangle cell, bool up)
    {
        int cx = cell.Left + cell.Width / 2, cy = cell.Top + cell.Height / 2;
        int halfWidth = S(4);
        int halfHeight = S(3);
        Point[] points = up
            ? new[]
            {
                new Point(cx - halfWidth, cy + S(2)), new Point(cx + halfWidth, cy + S(2)),
                new Point(cx, cy - halfHeight),
            }
            : new[]
            {
                new Point(cx - halfWidth, cy - S(2)), new Point(cx + halfWidth, cy - S(2)),
                new Point(cx, cy + halfHeight),
            };
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
