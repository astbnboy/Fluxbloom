using System.Text;

namespace Fluxbloom;

/// <summary>Small diagnostic log written to %APPDATA%\Fluxbloom\run.log (truncated each run, capped). Safe to ignore; never affects playback.</summary>
static class RunLog
{
    static StreamWriter? w;
    static long bytes;
    public static void Init(string mode)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            w = new StreamWriter(new FileStream(Path.Combine(Settings.Dir, "run.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Line($"Fluxbloom {typeof(RunLog).Assembly.GetName().Version} mode={mode} os={Environment.OSVersion.Version} dotnet={Environment.Version}");
        }
        catch { w = null; }
    }
    public static void Line(string s)
    {
        try { if (w == null || bytes > 200_000) return; var t = $"[{DateTime.Now:HH:mm:ss.fff}] {s}"; w.WriteLine(t); bytes += t.Length + 2; } catch { }
    }
}
