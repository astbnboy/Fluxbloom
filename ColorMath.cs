namespace Fluxbloom;

/// <summary>CPU mirror of the shader colour model: OKLCH (continuous, perceptual) to linear sRGB with hue-preserving gamut mapping.</summary>
public static class ColorMath
{
    public static (float r, float g, float b) OklchToLinearSrgb(float L, float C, float h)
    {
        float a = C * MathF.Cos(h), b = C * MathF.Sin(h);
        float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
        float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
        float s_ = L - 0.0894841775f * a - 1.2914855480f * b;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        float r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bl = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        float Y = MathF.Max(0.2126f * r + 0.7152f * g + 0.0722f * bl, 0f);
        float mn = MathF.Min(r, MathF.Min(g, bl));
        if (mn < 0f) { float k = Y / (Y - mn); r = Y + (r - Y) * k; g = Y + (g - Y) * k; bl = Y + (bl - Y) * k; }
        return (MathF.Max(r, 0f), MathF.Max(g, 0f), MathF.Max(bl, 0f));
    }

    public static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
