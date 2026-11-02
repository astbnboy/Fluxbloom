using System.Diagnostics;

namespace Fluxbloom;

/// <summary>Settings dialog (screensaver /c). All values persist to %APPDATA%\Fluxbloom\settings.json.</summary>
sealed class SettingsForm : Form
{
    readonly Settings original;
    Settings s;
    readonly Dictionary<string, Action<Settings>> appliers = new();
    readonly TableLayoutPanel grid = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 12, 12, 4) };

    static readonly string[] Styles = { "Mixed (everything)", "Flame / filaments", "Tendrils / branching", "Cellular / membranes", "Spirals / vortices", "Mathematical geometry only", "Fractals only (Mandelbulb, Julia)" };
    static readonly string[] Qualities = { "Low power (fewer particles, lower resolution, 60 fps cap)", "Balanced", "Maximum quality (native resolution, most detail)" };
    static readonly int[] Caps = { 0, 30, 60, 90, 120, 144, 165, 180, 240 };

    public SettingsForm(Settings settings)
    {
        original = settings; s = settings.Clone();
        Text = "Fluxbloom Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 640);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        Controls.Add(grid);

        Header("Look");
        Combo("Style", Styles, () => s.Style, v => s.Style = v);
        Slider("Geometry forms on screen (max)", 0, 3, () => s.MaxGeometry, v => s.MaxGeometry = v, "");
        Slider("Density", 20, 200, () => s.Density, v => s.Density = v, "%");
        Slider("Motion speed", 10, 250, () => s.Speed, v => s.Speed = v, "%");
        Slider("Brightness", 20, 120, () => s.Brightness, v => s.Brightness = v, "");
        Slider("Colour richness", 40, 160, () => s.ColorRichness, v => s.ColorRichness = v, "%");
        Slider("Colour drift", 0, 300, () => s.ColorCycle, v => s.ColorCycle = v, "%");

        Header("Performance and power");
        Combo("Quality / power", Qualities, () => s.Quality, v => s.Quality = v);
        var capLabels = Caps.Select(c => c == 0 ? "Match display refresh (max 240)" : $"{c} fps").ToArray();
        Combo("Frame-rate cap", capLabels, () => Math.Max(0, Array.IndexOf(Caps, s.FrameCap)), v => s.FrameCap = Caps[v]);
        Check("Sync to display refresh (V-Sync)", () => s.VSync, v => s.VSync = v);
        Check("Adaptive quality (auto-adjust to hold the frame rate)", () => s.AdaptiveQuality, v => s.AdaptiveQuality = v);

        Header("Display");
        Check("HDR10 / wide-gamut output when Windows HDR is on", () => s.WideGamutHdr, v => s.WideGamutHdr = v);
        Check("Also render on other monitors (otherwise they stay black)", () => s.RenderOtherDisplays, v => s.RenderOtherDisplays = v);

        Header("Advanced");
        Check("Show diagnostics overlay (fps, GPU timing, VRAM)", () => s.ShowDiagnostics, v => s.ShowDiagnostics = v);
        Number("Composition seed (0 = new each run)", () => s.Seed, v => s.Seed = v);

        var info = new Label
        {
            Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(14, 4, 14, 0), ForeColor = SystemColors.GrayText,
            Text = "Tip: with Quality = Maximum on a 5120x1440 / 240 Hz panel, leave Adaptive quality on. It lowers particle count first, then render scale, only when the GPU cannot hold the frame cap."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 6, 10, 6) };
        var ok = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var preview = new Button { Text = "Preview (windowed)", Width = 140 };
        var reset = new Button { Text = "Reset defaults", Width = 110 };
        ok.Click += (_, _) => { Commit(); Close(); };
        preview.Click += (_, _) => RunPreview();
        reset.Click += (_, _) => { s = new Settings(); RefreshAll(); };
        buttons.Controls.AddRange(new Control[] { ok, cancel, preview, reset });
        Controls.Add(info); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }

    void Commit() { s.Clamp(); s.Save(); }

    void RunPreview()
    {
        s.Clamp(); s.Save();
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "/w") { UseShellExecute = false }); } catch { }
    }

    void RefreshAll() { foreach (var a in appliers.Values) a(s); }

    void Header(string t)
    {
        grid.RowCount++;
        grid.Controls.Add(new Label { Text = t, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 10, 0, 2) }, 0, grid.RowCount - 1);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, grid.RowCount - 1)!, 3);
    }

    void Row(string label, Control c, Control? extra = null)
    {
        grid.RowCount++;
        int r = grid.RowCount - 1;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) }, 0, r);
        grid.Controls.Add(c, 1, r);
        if (extra != null) grid.Controls.Add(extra, 2, r);
    }

    void Slider(string label, int min, int max, Func<int> get, Action<int> set, string unit)
    {
        var tb = new TrackBar { Minimum = min, Maximum = max, TickFrequency = Math.Max(1, (max - min) / 10), Dock = DockStyle.Fill, Value = Math.Clamp(get(), min, max), AutoSize = false, Height = 30 };
        var val = new Label { Text = tb.Value + unit, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        tb.ValueChanged += (_, _) => { set(tb.Value); val.Text = tb.Value + unit; };
        appliers[label] = ns => { tb.Value = Math.Clamp(PropOf(ns, label, get), min, max); };
        Row(label, tb, val);
    }

    // The getter closures read the dialog's current 's'; after a reset we re-read via the same closures.
    int PropOf(Settings ns, string label, Func<int> get) => get();

    void Combo(string label, string[] items, Func<int> get, Action<int> set)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        cb.Items.AddRange(items);
        cb.SelectedIndex = Math.Clamp(get(), 0, items.Length - 1);
        cb.SelectedIndexChanged += (_, _) => set(cb.SelectedIndex);
        appliers[label] = _ => cb.SelectedIndex = Math.Clamp(get(), 0, items.Length - 1);
        Row(label, cb);
    }

    void Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        c.CheckedChanged += (_, _) => set(c.Checked);
        appliers[label] = _ => c.Checked = get();
        grid.RowCount++;
        grid.Controls.Add(c, 0, grid.RowCount - 1);
        grid.SetColumnSpan(c, 3);
    }

    void Number(string label, Func<int> get, Action<int> set)
    {
        var n = new NumericUpDown { Minimum = 0, Maximum = 999999, Value = get(), Width = 100 };
        n.ValueChanged += (_, _) => set((int)n.Value);
        appliers[label] = _ => n.Value = get();
        Row(label, n);
    }
}
