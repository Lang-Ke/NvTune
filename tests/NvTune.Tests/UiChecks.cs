using System.Drawing.Imaging;
using System.Reflection;
using SimpleNvidiaUndervolt;

internal static class UiChecks
{
    public static void Register(Action<string, Action> run, Action<bool> check)
    {
        run("DPI scaling has one owner and a 96 DPI design baseline", () =>
        {
            using var form = new MainForm(Array.Empty<string>(), preview: true);
            check(form.AutoScaleMode == AutoScaleMode.Dpi);
            check(Theme.Scaled(42) == 42);
            check(!typeof(MainForm).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Any(m => m.Name == "RescaleForDpi"));
        });
        run("Default design fits a compact 1080p window", () =>
        {
            using var form = new MainForm(Array.Empty<string>(), preview: true);
            check(form.ClientSize.Width <= 960 && form.ClientSize.Height <= 760);
        });
        run("Dark palette provides readable text and muted captions", () =>
        {
            check(Contrast(Theme.Text, Theme.Surface) >= 7);
            check(Contrast(Theme.Muted, Theme.Surface) >= 4.5);
            check(Contrast(Theme.Canvas, Theme.Accent) >= 4.5);
        });
        run("Rounded buttons completely erase old pixels outside their corners", () =>
        {
            var paint = typeof(ModernButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f })
                {
                    Theme.DpiScaleOverride = scale;
                    using var parent = new Panel { BackColor = Theme.Surface };
                    using var button = new ModernButton { Size = new Size(Theme.Scaled(112), Theme.Scaled(36)), Text = "恢复自动" };
                    parent.Controls.Add(button);
                    using var bitmap = new Bitmap(button.Width, button.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    foreach (var kind in new[] { ButtonKind.Secondary, ButtonKind.Primary })
                    {
                        button.Kind = kind;
                        graphics.Clear(Color.Magenta);
                        using var args = new PaintEventArgs(graphics, button.ClientRectangle);
                        paint.Invoke(button, new object[] { args });
                        foreach (var corner in new[] { Point.Empty, new Point(button.Width - 1, 0),
                            new Point(0, button.Height - 1), new Point(button.Width - 1, button.Height - 1) })
                            check(bitmap.GetPixel(corner.X, corner.Y).ToArgb() == parent.BackColor.ToArgb());
                    }
                }
            }
            finally { Theme.DpiScaleOverride = null; }
        });
        run("Drawing fonts scale once in pixels at 100 through 300 percent", () =>
        {
            using var plot = new FanCurveEditor();
            try
            {
                foreach (float scale in new[] { 1f, 1.5f, 1.75f, 2f, 2.5f, 3f })
                {
                    Theme.DpiScaleOverride = scale;
                    using var font = Theme.DrawingFont(plot, 9);
                    check(font.Unit == GraphicsUnit.Pixel && Math.Abs(font.Size - 12 * scale) < 0.01f);
                }
            }
            finally { Theme.DpiScaleOverride = null; }
        });
        run("Dark selectors support keyboard navigation and selection events", () =>
        {
            using var combo = new DarkCombo();
            combo.Items.AddRange(new object[] { "自动", "固定转速", "温度曲线" });
            int events = 0;
            combo.SelectedIndexChanged += (_, _) => events++;
            combo.SelectedIndex = 0;
            var key = typeof(DarkCombo).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            key.Invoke(combo, new object[] { new KeyEventArgs(Keys.Down) });
            check(combo.SelectedIndex == 1 && events == 2);
            key.Invoke(combo, new object[] { new KeyEventArgs(Keys.End) });
            check(combo.SelectedIndex == 2 && combo.AccessibleRole == AccessibleRole.ComboBox);
        });
    }

    internal static int Render(string directory)
    {
        // Deterministic software-only layout simulation: native child controls and custom
        // drawing are rendered together, without showing the form or triggering GPU startup.
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Directory.CreateDirectory(directory);
        foreach (float scale in new[] { 1f, 1.5f, 1.75f, 2f, 2.5f, 3f })
        {
            Theme.DpiScaleOverride = scale;
            using var form = new MainForm(Array.Empty<string>(), preview: true);
            form.AutoScaleMode = AutoScaleMode.None;
            form.FormBorderStyle = FormBorderStyle.None;
            form.MaximumSize = new Size(12000, 12000);
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-15000, -15000);
            form.ClientSize = new Size((int)(960 * scale), (int)(760 * scale));
            form.Show();
            LayoutTree(form);
            CheckLayout(form, scale);
            var viewport = Field<Panel>(form, "_viewport");
            if (viewport.VerticalScroll.Visible || viewport.HorizontalScroll.Visible)
                throw new Exception("Default compact window requires scrolling");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"nvtune-dark-{scale * 100:0}.png"), ImageFormat.Png);
            Console.WriteLine($"Rendered {scale * 100:0}%: {bitmap.Width}x{bitmap.Height}");
        }
        // Screen-sized viewports at large scaling require scrollable content while keeping
        // the save action outside the scrolling region.
        foreach (var scenario in new[] { (Scale: 1f, Width: 960, Height: 760, Scroll: false),
            (Scale: 1.25f, Width: 1200, Height: 950, Scroll: false),
            (Scale: 1.5f, Width: 1440, Height: 980, Scroll: true) })
        {
            Theme.DpiScaleOverride = scenario.Scale;
            using var form = new MainForm(Array.Empty<string>(), preview: true) { AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None, MaximumSize = new Size(12000, 12000), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-15000, -15000) };
            form.ClientSize = new Size(scenario.Width, scenario.Height);
            form.Show(); LayoutTree(form); CheckLayout(form, scenario.Scale);
            var viewport = Field<Panel>(form, "_viewport");
            if (viewport.VerticalScroll.Visible != scenario.Scroll || viewport.HorizontalScroll.Visible)
                throw new Exception("Unexpected scrollbars in 1080p compact layout");
            if (scenario.Scroll)
            {
                viewport.AutoScrollPosition = new Point(0, 10000);
                LayoutTree(form);
                var plot = Field<FanCurveEditor>(form, "_curveEditor");
                var visiblePlot = viewport.RectangleToClient(plot.RectangleToScreen(plot.ClientRectangle));
                if (!visiblePlot.IntersectsWith(viewport.ClientRectangle)) throw new Exception("Cannot scroll to fan curve");
            }
            Console.WriteLine($"PASS 1080p compact window at {scenario.Scale * 100:0}%: save and curve reachable");
        }
        foreach (var scenario in new[] { (Scale: 2f, Width: 1920, Height: 1000), (Scale: 3f, Width: 3840, Height: 2050), (Scale: 2.5f, Width: 2560, Height: 1350) })
        {
            Theme.DpiScaleOverride = scenario.Scale;
            using var form = new MainForm(Array.Empty<string>(), preview: true) { AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None, MaximumSize = new Size(12000, 12000), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-15000, -15000) };
            form.ClientSize = new Size(scenario.Width, scenario.Height);
            form.Show();
            LayoutTree(form);
            CheckLayout(form, scenario.Scale);
            var viewport = Field<Panel>(form, "_viewport");
            if (scenario.Height < 750 * scenario.Scale && !viewport.VerticalScroll.Visible)
                throw new Exception("Expected vertical scroll at high scaling");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"nvtune-dark-{scenario.Width}-{scenario.Scale * 100:0}.png"), ImageFormat.Png);
            Console.WriteLine($"PASS {scenario.Width}x{scenario.Height} at {scenario.Scale * 100:0}%: scroll and save action accessible");
        }
        Theme.DpiScaleOverride = 1;
        using (var large = new MainForm(Array.Empty<string>(), preview: true) { AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None, MaximumSize = new Size(12000, 12000), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-15000, -15000) })
        {
            large.ClientSize = new Size(3840, 2050);
            large.Show();
            LayoutTree(large);
            if (Field<FanCurveEditor>(large, "_curveEditor").Height < 1000) throw new Exception("Plot does not expand on a large display");
            CheckLayout(large, 1);
            Console.WriteLine("PASS 3840x2050 at 100%: plot expands with the viewport");
        }
        Theme.DpiScaleOverride = null;
        return 0;
    }

    private static T Field<T>(MainForm form, string name)
        => (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

    private static void CheckLayout(MainForm form, float scale)
    {
        var content = Field<TableLayoutPanel>(form, "_content");
        var viewport = Field<Panel>(form, "_viewport");
        var plot = Field<FanCurveEditor>(form, "_curveEditor");
        if (plot.Width < 360 * scale || plot.Height < 170 * scale) throw new Exception("Plot clipped");
        foreach (string field in new[] { "_mvInput", "_mhzInput", "_memInput" })
        {
            var input = Field<DarkNumeric>(form, field);
            if (Math.Abs(input.Height - 36 * scale) > 1) throw new Exception($"Numeric field height: {input.Height}");
            var editor = input.Controls.OfType<TextBox>().Single();
            if (editor.Height > input.Height || editor.Width < 40 * scale) throw new Exception("Numeric editor clipped");
        }
        var footerSave = Descendants(form).OfType<Button>().Single(b => b.Text == "保存并开机应用");
        var saveBounds = form.RectangleToClient(footerSave.RectangleToScreen(footerSave.ClientRectangle));
        if (!form.ClientRectangle.Contains(saveBounds)) throw new Exception("Save action outside viewport");
        foreach (var table in Descendants(content).OfType<TableLayoutPanel>())
        {
            var buttons = table.Controls.OfType<Button>().ToArray();
            for (int i = 0; i < buttons.Length; i++)
                for (int j = i + 1; j < buttons.Length; j++)
                    if (buttons[i].Bounds.IntersectsWith(buttons[j].Bounds)) throw new Exception("Buttons overlap");
        }
        if (content.Width < 860 * scale || viewport.Width <= 0) throw new Exception("Invalid scroll extent");
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void LayoutTree(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutTree(child);
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double V(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * V(c.R) + 0.7152 * V(c.G) + 0.0722 * V(c.B);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
