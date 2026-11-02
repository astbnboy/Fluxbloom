using System.Text.Json;

namespace Fluxbloom;

/// <summary>User settings, persisted as JSON in %APPDATA%\Fluxbloom\settings.json.</summary>
public sealed class Settings
{
    public int Style { get; set; } = 0;            // 0 Mixed, 1 Flame, 2 Tendrils, 3 Cellular, 4 Spiral, 5 Math geometry, 6 Fractals
    public int Geometry { get; set; } = 75;        // 0..100: share of forms that are mathematical geometry (Mixed style)
    public int Speed { get; set; } = 100;          // 10..250 (% of base motion speed)
    public int Density { get; set; } = 100;        // 20..200 (% of base number of forms / particles)
    public int Brightness { get; set; } = 80;      // 20..120
    public int ColorRichness { get; set; } = 100;  // 40..160 (% chroma)
    public int ColorCycle { get; set; } = 100;     // 0..300 (% hue drift speed)
    public int Quality { get; set; } = 1;          // 0 Low power, 1 Balanced, 2 Maximum quality
    public int FrameCap { get; set; } = 0;         // 0 = display refresh (max 240), else fps cap
    public bool VSync { get; set; } = true;
    public bool AdaptiveQuality { get; set; } = true;
    public bool WideGamutHdr { get; set; } = true; // use HDR10 / Rec.2020 output only when Windows HDR is on
    public bool RenderOtherDisplays { get; set; } = false;
    public bool ShowDiagnostics { get; set; } = false;
    public int Seed { get; set; } = 0;             // 0 = new composition every run

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fluxbloom");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Clamp()
    {
        Style = Math.Clamp(Style, 0, 6);
        Geometry = Math.Clamp(Geometry, 0, 100);
        Speed = Math.Clamp(Speed, 10, 250);
        Density = Math.Clamp(Density, 20, 200);
        Brightness = Math.Clamp(Brightness, 20, 120);
        ColorRichness = Math.Clamp(ColorRichness, 40, 160);
        ColorCycle = Math.Clamp(ColorCycle, 0, 300);
        Quality = Math.Clamp(Quality, 0, 2);
        FrameCap = FrameCap <= 0 ? 0 : Math.Clamp(FrameCap, 15, 240);
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}
