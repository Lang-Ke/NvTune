using System.Collections.ObjectModel;
using System.Drawing.Drawing2D;

namespace SimpleNvidiaUndervolt;

/// <summary>Paints both the field and popup with the dark palette, including the arrow.</summary>
internal sealed class DarkCombo : Control
{
    internal sealed class ItemCollection : Collection<object>
    {
        public void AddRange(object[] values) { foreach (var value in values) Add(value); }
    }
    private int _selectedIndex = -1;
    private ToolStripDropDown? _popup;
    private bool _hover;
    public DarkCombo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Height = Theme.Scaled(36);
        BackColor = Theme.Neutral;
        ForeColor = Theme.Text;
        AccessibleRole = AccessibleRole.ComboBox;
        Cursor = Cursors.Hand;
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ItemCollection Items { get; } = new();
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= Items.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            AccessibleName = value < 0 ? "选择" : Items[value].ToString();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? SelectedIndexChanged;
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        if (_popup?.Visible == true) _popup.Close(); else OpenPopup();
    }
    protected override bool IsInputKey(Keys keyData)
        => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Items.Count == 0) return;
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || e.Alt && e.KeyCode == Keys.Down) OpenPopup();
        else if (e.KeyCode == Keys.Down) SelectedIndex = Math.Min(Items.Count - 1, SelectedIndex + 1);
        else if (e.KeyCode == Keys.Up) SelectedIndex = Math.Max(0, SelectedIndex - 1);
        else if (e.KeyCode == Keys.Home) SelectedIndex = 0;
        else if (e.KeyCode == Keys.End) SelectedIndex = Items.Count - 1;
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }
    private void OpenPopup()
    {
        if (Items.Count == 0 || _popup?.Visible == true) return;
        _popup?.Dispose();
        int itemHeight = Math.Max(Theme.Pixels(this, 36), Font.Height + Theme.Pixels(this, 12));
        var list = new ListBox
        {
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
            Font = Font, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = itemHeight,
            IntegralHeight = false, Width = Math.Max(1, Width - Theme.Pixels(this, 4)), Height = itemHeight * Math.Min(Items.Count, 7),
        };
        list.Items.AddRange(Items.ToArray());
        list.SelectedIndex = SelectedIndex;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.AccentSoft : Theme.Surface);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString(), Font,
                Rectangle.Inflate(e.Bounds, -Theme.Pixels(this, 12), 0), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
        var popup = new ToolStripDropDown { AutoSize = true, Padding = new Padding(Theme.Pixels(this, 2)), BackColor = Theme.Border };
        var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
        popup.Items.Add(host);
        void Choose() { if (list.SelectedIndex >= 0) SelectedIndex = list.SelectedIndex; popup.Close(); Focus(); }
        list.MouseClick += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Choose(); e.Handled = true; } else if (e.KeyCode == Keys.Escape) { popup.Close(); Focus(); e.Handled = true; } };
        _popup = popup;
        popup.Show(this, new Point(0, Height + Theme.Pixels(this, 4)));
        list.Focus();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Pixels(this, 7));
        using var fill = new SolidBrush(_hover ? Theme.NeutralHover : Theme.Neutral);
        using var line = new Pen(Focused ? Theme.Accent : Theme.Border);
        g.FillPath(fill, path); g.DrawPath(line, path);
        var text = new Rectangle(Theme.Pixels(this, 12), 0, Math.Max(1, Width - Theme.Pixels(this, 44)), Height);
        TextRenderer.DrawText(g, SelectedIndex >= 0 ? Items[SelectedIndex]?.ToString() : "选择显卡", Font, text,
            Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        int x = Width - Theme.Pixels(this, 20), y = Height / 2;
        using var arrow = new Pen(Theme.Muted, Math.Max(1, Theme.ScaleFor(this) * 1.5f));
        g.DrawLines(arrow, new[] { new Point(x - Theme.Pixels(this, 4), y - Theme.Pixels(this, 2)), new Point(x, y + Theme.Pixels(this, 2)), new Point(x + Theme.Pixels(this, 4), y - Theme.Pixels(this, 2)) });
    }
    protected override void Dispose(bool disposing) { if (disposing) _popup?.Dispose(); base.Dispose(disposing); }
}
