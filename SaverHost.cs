using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fluxbloom;

/// <summary>Borderless per-monitor window that hosts either a D3D12 swapchain or plain black.</summary>
sealed class SaverForm : Form
{
    readonly bool exitOnInput;
    Point? firstMouse;
    readonly Stopwatch since = Stopwatch.StartNew();
    public volatile bool ExitRequested;

    public SaverForm(Rectangle bounds, bool exitOnInput, bool topMost = true)
    {
        this.exitOnInput = exitOnInput;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = topMost;
        BackColor = Color.Black;
        Bounds = bounds;
        KeyPreview = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Color.Black); }

    protected override void OnKeyDown(KeyEventArgs e) { if (exitOnInput || e.KeyCode == Keys.Escape) ExitRequested = true; base.OnKeyDown(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (exitOnInput) ExitRequested = true; base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (exitOnInput && since.ElapsedMilliseconds > 800)
        {
            var p = Cursor.Position;
            firstMouse ??= p;
            if (Math.Abs(p.X - firstMouse.Value.X) > 6 || Math.Abs(p.Y - firstMouse.Value.Y) > 6) ExitRequested = true;
        }
        else if (exitOnInput) firstMouse = Cursor.Position;
        base.OnMouseMove(e);
    }
    protected override void OnDeactivate(EventArgs e) { if (exitOnInput && since.ElapsedMilliseconds > 1500) ExitRequested = true; base.OnDeactivate(e); }
    protected override bool ShowWithoutActivation => false;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x0020; /* CS_OWNDC not needed but harmless */ return cp; } }
}

static class SaverHost
{
    sealed class Surface
    {
        public SaverForm Form = null!;
        public Renderer? R;
        public Director? D;
        public double NextDue;
    }

    public static Screen PickMainScreen()
    {
        Screen? best = null; double bestAspect = 0;
        foreach (var s in Screen.AllScreens)
        {
            double a = (double)s.Bounds.Width / s.Bounds.Height;
            if (a > bestAspect + 0.05) { best = s; bestAspect = a; }
        }
        if (best != null && bestAspect >= 2.8) return best;
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    /// <summary>Full screensaver: fullscreen on every monitor; the ultrawide gets the full renderer.</summary>
    public static int RunFullscreen(Settings s, bool windowedDebug)
    {
        bool exitOnInput = !windowedDebug;
        var surfaces = new List<Surface>();
        var main = PickMainScreen();
        var screens = Screen.AllScreens;
        if (!exitOnInput) screens = new[] { main };
        bool exit = false;
        SystemEvents.DisplaySettingsChanged += (_, _) => { if (exitOnInput) exit = true; };
        SystemEvents.SessionSwitch += (_, _) => { if (exitOnInput) exit = true; };

        if (exitOnInput) Cursor.Hide();
        try
        {
            foreach (var scr in screens)
            {
                Rectangle b = scr.Bounds;
                if (windowedDebug) b = new Rectangle(scr.Bounds.X + 80, scr.Bounds.Y + 80, Math.Min(scr.Bounds.Width - 160, 2560), Math.Min(scr.Bounds.Height - 160, 720));
                var sf = new Surface { Form = new SaverForm(b, exitOnInput, topMost: exitOnInput) };
                if (windowedDebug) { sf.Form.FormBorderStyle = FormBorderStyle.SizableToolWindow; sf.Form.Text = "Fluxbloom (debug window, Esc to quit)"; }
                sf.Form.Show();
                sf.Form.Bounds = b;
                bool isMain = scr.DeviceName == main.DeviceName;
                if (isMain || s.RenderOtherDisplays)
                {
                    var cs = sf.Form.ClientSize;
                    float aspect = (float)cs.Width / cs.Height;
                    sf.D = new Director(s, aspect, s.Seed == 0 ? 0 : s.Seed + surfaces.Count);
                    float hz = NativeMethods.RefreshOf(scr.DeviceName);
                    var cfg = isMain ? s : OtherDisplaySettings(s);
                    sf.R = new Renderer(sf.Form.Handle, cs.Width, cs.Height, cfg, sf.D, false, hz, isMain ? "main" : scr.DeviceName.Replace("\\\\.\\", ""), manualPacing: !isMain);
                }
                surfaces.Add(sf);
            }
            if (exitOnInput) surfaces[0].Form.Activate();

            var clock = Stopwatch.StartNew();
            double last = 0;
            while (!exit)
            {
                Application.DoEvents();
                if (surfaces.Any(x => x.Form.ExitRequested || x.Form.IsDisposed)) break;
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)(now - last); last = now;
                bool anyDrawn = false;
                foreach (var sf in surfaces)
                {
                    if (sf.R == null) continue;
                    if (sf.Form.WindowState == FormWindowState.Minimized || !sf.Form.Visible) continue;
                    if (sf.R.ManualPacing) { if (now < sf.NextDue) continue; sf.NextDue = now + 1.0 / 60.0; sf.R.RenderFrame(1f / 60f); anyDrawn = true; }
                    else { sf.R.RenderFrame(dt); anyDrawn = true; }
                }
                if (!anyDrawn) Thread.Sleep(50);
                else if (surfaces.All(x => x.R == null || x.R.ManualPacing)) Thread.Sleep(2);
            }
        }
        finally
        {
            foreach (var sf in surfaces) { try { sf.R?.Dispose(); } catch { } try { sf.Form.Close(); } catch { } }
            if (exitOnInput) Cursor.Show();
        }
        return 0;
    }

    static Settings OtherDisplaySettings(Settings s)
    {
        var c = s.Clone(); c.Quality = 0; c.FrameCap = 60; c.VSync = false; c.ShowDiagnostics = false; c.WideGamutHdr = false; return c;
    }

    /// <summary>Preview inside the Windows screensaver settings thumbnail (/p hwnd).</summary>
    public static int RunPreview(Settings s, IntPtr parent)
    {
        if (!NativeMethods.GetClientRect(parent, out var rc)) return 1;
        int w = Math.Max(160, rc.Right - rc.Left), h = Math.Max(90, rc.Bottom - rc.Top);
        var form = new SaverForm(new Rectangle(0, 0, w, h), exitOnInput: false, topMost: false) { FormBorderStyle = FormBorderStyle.None };
        form.Show();
        NativeMethods.SetWindowLong(form.Handle, -16, unchecked((int)0x40000000) | 0x10000000); // WS_CHILD | WS_VISIBLE
        NativeMethods.SetParent(form.Handle, parent);
        form.Bounds = new Rectangle(0, 0, w, h);
        var ps = s.Clone(); ps.Quality = 0; ps.FrameCap = 30; ps.VSync = false; ps.ShowDiagnostics = false; ps.WideGamutHdr = false; ps.Density = Math.Min(ps.Density, 60);
        var dir = new Director(ps, (float)w / h, ps.Seed);
        using var r = new Renderer(form.Handle, w, h, ps, dir, false, 60f, "preview", manualPacing: true);
        var clock = Stopwatch.StartNew(); double next = 0;
        while (NativeMethods.IsWindow(parent) && !form.IsDisposed)
        {
            Application.DoEvents();
            double now = clock.Elapsed.TotalSeconds;
            if (now < next) { Thread.Sleep(4); continue; }
            next = now + 1.0 / 30.0;
            r.RenderFrame(1f / 30f);
        }
        return 0;
    }

    /// <summary>Offscreen benchmark / capture at the monitor's native size: proves GPU execution without touching the display.</summary>
    public static int RunBench(Settings s, int frames, string? pngPath, int w, int h, string logPath)
    {
        var dir = new Director(s, (float)w / h, s.Seed != 0 ? s.Seed : 20261101);
        using var r = new Renderer(IntPtr.Zero, w, h, s, dir, true, 240f, "bench");
        var wall = Stopwatch.StartNew();
        double gpuSum = 0, simSum = 0, fieldSum = 0, postSum = 0; int n = 0;
        for (int i = 0; i < frames; i++)
        {
            if (i == frames - 1) r.RequestCapture();
            r.RenderFrame(1f / 60f);
            if (i >= frames - 120) { gpuSum += r.Stats.GpuMs; simSum += r.Stats.GpuSimMs; fieldSum += r.Stats.GpuFieldMs; postSum += r.Stats.GpuPostMs; n++; }
        }
        wall.Stop();
        var st = r.Stats;
        string report =
            $"[{DateTime.Now:s}] bench {w}x{h} frames={frames} quality={s.Quality} density={s.Density}\n" +
            $"  GPU: {st.Gpu}\n  render {st.RenderW}x{st.RenderH} (scale {st.Scale:F2}) particles {st.Particles:N0} forms {st.Forms}\n" +
            $"  GPU ms/frame (last {n}): total {gpuSum / n:F3}  sim {simSum / n:F3}  scene {fieldSum / n:F3}  post {postSum / n:F3}  => {1000.0 / (gpuSum / n):F0} fps GPU-bound\n" +
            $"  wall: {wall.Elapsed.TotalSeconds:F2}s for {frames} frames = {frames / wall.Elapsed.TotalSeconds:F1} fps incl. CPU\n" +
            $"  VRAM: {st.VramUsed / 1048576.0:F0} MB used of {st.VramBudget / 1048576.0:F0} MB budget\n";
        File.AppendAllText(logPath, report);
        if (pngPath != null && r.TryGetCapture(out var bgra, out int cw, out int ch))
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
            bmp.UnlockBits(bd);
            bmp.Save(pngPath, ImageFormat.Png);
            File.AppendAllText(logPath, $"  saved {pngPath}\n");
        }
        return 0;
    }
}
