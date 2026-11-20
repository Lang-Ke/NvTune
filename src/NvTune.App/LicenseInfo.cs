namespace SimpleNvidiaUndervolt;

/// <summary>The license travels inside the executable, including when distributed on its own.</summary>
internal static class LicenseInfo
{
    internal static string Text { get; } = ReadText();

    private static string ReadText()
    {
        using var stream = typeof(LicenseInfo).Assembly.GetManifestResourceStream("NvTune.LICENSE")
            ?? throw new InvalidOperationException("The embedded NvTune license is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void Show(IWin32Window owner)
    {
        using var dialog = new Form
        {
            Text = "关于 NvTune · 开源许可证",
            AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(660, 460), MinimumSize = new Size(420, 300),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false,
            BackColor = Theme.Canvas, ForeColor = Theme.Text, Font = Theme.BodyFont(),
            Padding = new Padding(16),
        };
        var content = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = true, Text = Text,
            BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
        };
        var version = new Label
        {
            Text = $"NvTune {Product.Version}", Dock = DockStyle.Top, Height = 36,
            ForeColor = Theme.Accent, Font = Theme.CardFont(),
        };
        var close = Theme.SecondaryButton("关闭", (_, _) => dialog.Close());
        close.Dock = DockStyle.Bottom;
        dialog.Controls.Add(content);
        dialog.Controls.Add(version);
        dialog.Controls.Add(close);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(dialog).WorkingArea;
            dialog.MinimumSize = new Size(Math.Min(dialog.MinimumSize.Width, area.Width - 24),
                Math.Min(dialog.MinimumSize.Height, area.Height - 24));
            dialog.Size = new Size(Math.Min(dialog.Width, area.Width - 24), Math.Min(dialog.Height, area.Height - 24));
            dialog.Location = new Point(Math.Clamp(dialog.Left, area.Left, Math.Max(area.Left, area.Right - dialog.Width)),
                Math.Clamp(dialog.Top, area.Top, Math.Max(area.Top, area.Bottom - dialog.Height)));
            content.Select(0, 0);
        };
        dialog.ShowDialog(owner);
    }
}
