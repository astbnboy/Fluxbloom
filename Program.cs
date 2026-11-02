namespace Fluxbloom;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        NativeMethods.timeBeginPeriod(1);
        try
        {
            string mode = "/c";
            string? arg = null;
            if (args.Length > 0)
            {
                mode = args[0].ToLowerInvariant();
                int colon = mode.IndexOf(':');
                if (colon > 0) { arg = args[0][(colon + 1)..]; mode = mode[..colon]; }
                else if (args.Length > 1) arg = args[1];
                if (mode.StartsWith('-')) mode = "/" + mode[1..];
            }

            var settings = Settings.Load();
            { var ty = Environment.GetEnvironmentVariable("FLX_TYPES"); if (!string.IsNullOrWhiteSpace(ty)) Director.DebugTypes = ty.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); }
            { var e = Environment.GetEnvironmentVariable("FLX_INJECT"); if (float.TryParse(e, out var v)) Renderer.InjectBase = v; e = Environment.GetEnvironmentVariable("FLX_HAZE"); if (float.TryParse(e, out v)) Renderer.HazeBase = v; e = Environment.GetEnvironmentVariable("FLX_BLOOM"); if (float.TryParse(e, out v)) Renderer.BloomBase = v; }
            switch (mode)
            {
                case "/s": // screensaver
                    return SaverHost.RunFullscreen(settings, windowedDebug: false);
                case "/w": // windowed debug run (Esc quits, mouse/keys don't)
                    settings.ShowDiagnostics = true;
                    return SaverHost.RunFullscreen(settings, windowedDebug: true);
                case "/dirtest": // headless: simulate the director for hours and check the 1-3 geometry rule
                    return DirectorTest.Run(settings, args.Length > 1 && int.TryParse(args[1], out var hrs) ? hrs : 4);
                case "/p": // preview in the Windows settings dialog
                    return long.TryParse(arg, out long h) ? SaverHost.RunPreview(settings, new IntPtr(h)) : 1;
                case "/bench": // /bench <frames> <out.png> [quality 0-2] [density]
                {
                    int frames = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 600;
                    string? png = args.Length > 2 ? args[2] : null;
                    if (args.Length > 3 && int.TryParse(args[3], out var q)) settings.Quality = q;
                    if (args.Length > 4 && int.TryParse(args[4], out var d)) settings.Density = d;
                    settings.Clamp();
                    string log = Path.Combine(Settings.Dir, "bench.txt");
                    Directory.CreateDirectory(Settings.Dir);
                    try { return SaverHost.RunBench(settings, frames, png, 5120, 1440, log); }
                    catch (Exception ex) { File.AppendAllText(log, $"[{DateTime.Now:s}] bench FAILED: {ex}\n"); return 2; }
                }
                default: // /c or no args: configuration dialog
                    Application.Run(new SettingsForm(settings));
                    return 0;
            }
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:s}] {ex}\n"); } catch { }
            return 1;
        }
        finally { NativeMethods.timeEndPeriod(1); }
    }
}
