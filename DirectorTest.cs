namespace Fluxbloom;

/// <summary>Headless check of the composition scheduler: never more than 3 (and, if enabled, at least 1) geometry forms visible.</summary>
static class DirectorTest
{
    public static int Run(Settings s, int hours)
    {
        Directory.CreateDirectory(Settings.Dir);
        var log = Path.Combine(Settings.Dir, "dirtest.txt");
        var lines = new List<string>();
        int bad = 0;
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var d = new Director(s, 5120f / 1440f, seed);
            int maxVis = 0, minVis = 99, maxSlots = 0, maxFlow = 0; double sumVis = 0; long n = 0;
            float dt = 0.1f;
            var types = new Dictionary<int, int>();
            int prev = -1;
            for (long i = 0; i < hours * 3600L * 10; i++)
            {
                d.Update(dt);
                if (d.Time < 20f) continue;               // ignore the first 20 s fade-in
                int v = d.VisibleGeometry;
                maxVis = Math.Max(maxVis, v); minVis = Math.Min(minVis, v); sumVis += v; n++;
                maxSlots = Math.Max(maxSlots, d.Count);
                maxFlow = Math.Max(maxFlow, d.ActiveForms - d.GeometryCount);
                if (v == 0 && s.MaxGeometry > 0 && lines.Count(l => l.StartsWith("  first zero seed " + seed)) < 2) lines.Add("  first zero seed " + seed + ": " + d.DebugState());
            }
            bool ok = maxVis <= 3 && (s.MaxGeometry == 0 || minVis >= 1);
            if (!ok) bad++;
            lines.Add($"seed {seed}: {hours} h simulated, visible geometry min {minVis} max {maxVis} avg {sumVis / n:F2}, max slots {maxSlots}, {(ok ? "OK" : "VIOLATION")}");
        }
        File.WriteAllLines(log, lines);
        return bad;
    }
}
