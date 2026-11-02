namespace Fluxbloom;

/// <summary>
/// CPU-side composition director. It owns a pool of up to 24 "origins" (independent generative forms). Each origin has its own
/// position anywhere on the 32:9 canvas, scale, anisotropy, symmetry, flow style and colour (continuous OKLCH), a lifecycle with
/// long smooth fades, and slow parameter wander. A coverage grid steers new origins toward the least-covered part of the canvas
/// so there are no persistent dead zones. It emits a flat float array that mirrors the HLSL Origin struct (24 floats each).
/// </summary>
public sealed class Director
{
    public const int Max = 24;
    public const int FloatsPerOrigin = 24;
    const int GX = 64, GY = 16;

    sealed class Org
    {
        public float X, Y, VX, VY;
        public float Radius, Stretch, Rot, RotVel, Sym;
        public float Kind0, KindAmp, KindW, KindPh;
        public float Hue, HueRate, Chroma, Light, Spread;
        public float Radial, Twist, Density, Freq, GradX, GradY, Phase;
        public float Birth, Life, FadeIn, FadeOut;
        public float Ph1, Ph2, Ph3;
        public Org? MergeInto;
        public float Weight, KindNow, RadiusNow, TwistNow;
    }

    readonly Random rng;
    readonly Settings set;
    readonly float aspect;
    readonly List<Org> orgs = new();
    readonly float[] cover = new float[GX * GY];
    float time, nextSpawn = 0.5f, nextEvent = 28f;
    float globalHue;
    public readonly float[] Gpu = new float[Max * FloatsPerOrigin];
    public int Count { get; private set; }
    public float GlobalFade { get; private set; }
    public float Time => time;
    public int ActiveForms => orgs.Count;

    public Director(Settings s, float aspect, int seed)
    {
        set = s; this.aspect = aspect;
        rng = seed != 0 ? new Random(seed) : new Random();
        globalHue = (float)(rng.NextDouble() * Math.PI * 2);
        int n = Math.Max(7, (int)MathF.Round(TargetCount(0f) * 1.25f));
        for (int i = 0; i < n; i++) Spawn(initial: true);
    }

    float F() => (float)rng.NextDouble();
    float R(float a, float b) => a + (b - a) * F();

    float TargetCount(float t)
    {
        float m = 0.5f + 0.5f * MathF.Sin(t * 0.0261f + 0.7f) * MathF.Sin(t * 0.0113f + 2.1f + globalHue);
        m = ColorMath.Smooth(0.0f, 1.0f, m);
        float dens = set.Density / 100f;
        return Math.Clamp((3f + 14f * m) * dens, 2f, 22f);
    }

    float PickKind()
    {
        // continuous kinds: 0 spiral, 1 sinusoidal flame, 2 julia-ish, 3 tendrils, 4 cellular; fractional parts crossfade neighbours.
        float[] pool = set.Style switch
        {
            1 => new[] { 1f, 2f, 1.5f, 0.5f },
            2 => new[] { 3f, 2.5f, 3.5f, 0.5f },
            3 => new[] { 4f, 3.5f, 4f, 3f },
            4 => new[] { 0f, 0.5f, 2f, 1f },
            _ => new[] { 0f, 1f, 2f, 3f, 4f, 0.5f, 1.5f, 2.5f, 3.5f }
        };
        return Math.Clamp(pool[rng.Next(pool.Length)] + R(-0.25f, 0.25f), 0f, 4f);
    }

    (float x, float y) PickPosition(bool initial)
    {
        if (initial && F() < 0.5f) return (R(-aspect, aspect), R(-1f, 1f));
        float bx = 0, by = 0, best = float.MaxValue;
        for (int k = 0; k < 8; k++)
        {
            int cx = rng.Next(GX), cy = rng.Next(GY);
            float c = cover[cy * GX + cx] + F() * 0.05f;
            if (c < best) { best = c; bx = ((cx + F()) / GX * 2f - 1f) * aspect; by = 1f - (cy + F()) / GY * 2f; }
        }
        if (F() < 0.14f) // edges and corners on purpose
        {
            if (F() < 0.6f) bx = (F() < 0.5f ? -1f : 1f) * aspect * R(0.88f, 1.08f);
            if (F() < 0.6f) by = (F() < 0.5f ? -1f : 1f) * R(0.82f, 1.08f);
        }
        return (bx, by);
    }

    void Spawn(bool initial = false, Org? parent = null)
    {
        if (orgs.Count >= Max) return;
        var o = new Org();
        var (px, py) = PickPosition(initial);
        o.X = px; o.Y = py;
        if (parent != null) { float a = R(0, 6.2832f); o.X = parent.X + MathF.Cos(a) * parent.Radius * 0.9f; o.Y = parent.Y + MathF.Sin(a) * parent.Radius * 0.9f; }
        float sp = set.Speed / 100f;
        o.VX = R(-0.020f, 0.020f) * sp; o.VY = R(-0.012f, 0.012f) * sp;
        o.Radius = MathF.Exp(R(MathF.Log(0.22f), MathF.Log(1.25f)));
        o.Stretch = F() < 0.45f ? R(1.6f, 3.8f) : R(0.75f, 1.5f);
        o.Rot = F() < 0.5f ? R(-0.25f, 0.25f) : R(0, 6.2832f);
        o.RotVel = R(-0.035f, 0.035f) * sp;
        float[] syms = { 1, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12 };
        o.Sym = syms[rng.Next(syms.Length)];
        o.Kind0 = parent != null ? Math.Clamp(parent.Kind0 + R(-1f, 1f), 0f, 4f) : PickKind();
        o.KindAmp = R(0.1f, 0.6f); o.KindW = R(0.01f, 0.05f); o.KindPh = R(0, 6.28f);
        // palette: continuous hue relationships around a slowly rotating global hue (analogous / complement / triad / free)
        float[] rel = { 0f, 0.4f, -0.4f, 3.1416f, 2.0944f, -2.0944f, 1.0f, -1.0f };
        o.Hue = globalHue + rel[rng.Next(rel.Length)] + R(-0.35f, 0.35f);
        if (parent != null) o.Hue = parent.Hue + R(0.15f, 0.7f);
        o.HueRate = R(-0.02f, 0.02f);
        o.Chroma = R(0.07f, 0.21f);
        o.Light = R(0.62f, 0.84f);
        o.Spread = R(0.2f, 1.7f);
        o.Radial = R(-0.9f, 0.9f);
        o.Twist = R(-1.6f, 1.6f);
        o.Density = R(0.6f, 1.4f);
        o.Freq = R(2f, 6.5f);
        o.GradX = R(-0.9f, 0.9f); o.GradY = R(-0.9f, 0.9f);
        o.Phase = R(0, 6.28f);
        o.Life = R(55f, 170f);
        o.FadeIn = initial ? 0.01f : R(8f, 16f);
        o.FadeOut = R(10f, 22f);
        o.Birth = initial ? time - R(0f, o.Life * 0.45f) : time;
        o.Ph1 = R(0, 6.28f); o.Ph2 = R(0, 6.28f); o.Ph3 = R(0, 6.28f);
        orgs.Add(o);
    }

    public void Update(float dt)
    {
        time += dt;
        float sp = set.Speed / 100f;
        globalHue += dt * 0.0026f * (set.ColorCycle / 100f);
        GlobalFade = ColorMath.Smooth(0f, 3.5f, time);

        // coverage grid: decay, then add each origin's footprint
        float decay = MathF.Exp(-dt / 22f);
        for (int i = 0; i < cover.Length; i++) cover[i] *= decay;

        for (int i = orgs.Count - 1; i >= 0; i--)
        {
            var o = orgs[i];
            float age = time - o.Birth, end = o.Life;
            float w = ColorMath.Smooth(0f, o.FadeIn, age) * (1f - ColorMath.Smooth(end - o.FadeOut, end, age));
            o.Weight = w;
            if (age > end) { orgs.RemoveAt(i); continue; }

            // drift with soft pull back if far outside the canvas
            o.X += o.VX * dt; o.Y += o.VY * dt;
            float ox = MathF.Abs(o.X) - aspect * 1.15f, oy = MathF.Abs(o.Y) - 1.15f;
            if (ox > 0) o.VX -= MathF.Sign(o.X) * ox * 0.02f * dt;
            if (oy > 0) o.VY -= MathF.Sign(o.Y) * oy * 0.02f * dt;
            o.Rot += o.RotVel * dt;
            o.Hue += o.HueRate * dt * (set.ColorCycle / 100f);
            o.KindNow = Math.Clamp(o.Kind0 + o.KindAmp * MathF.Sin(time * o.KindW + o.KindPh), 0f, 4f);
            o.RadiusNow = o.Radius * (1f + 0.16f * MathF.Sin(time * 0.071f + o.Ph1) + 0.07f * MathF.Sin(time * 0.173f + o.Ph2));
            o.TwistNow = o.Twist * (0.75f + 0.25f * MathF.Sin(time * 0.05f + o.Ph3));

            if (o.MergeInto != null)
            {
                var t = o.MergeInto;
                o.X += (t.X - o.X) * 0.12f * dt; o.Y += (t.Y - o.Y) * 0.12f * dt;
                float dh = t.Hue - o.Hue; o.Hue += dh * 0.05f * dt;
                t.Hue -= dh * 0.01f * dt;
            }

            // footprint
            if (w > 0.02f)
            {
                float rx = o.RadiusNow * MathF.Max(o.Stretch, 1f) * 1.2f, ry = o.RadiusNow * 1.2f;
                int x0 = Math.Max(0, (int)((o.X - rx) / aspect * 0.5f * GX + GX * 0.5f)), x1 = Math.Min(GX - 1, (int)((o.X + rx) / aspect * 0.5f * GX + GX * 0.5f));
                int y0 = Math.Max(0, (int)((1f - (o.Y + ry)) * 0.5f * GY)), y1 = Math.Min(GY - 1, (int)((1f - (o.Y - ry)) * 0.5f * GY));
                for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++) cover[cy * GX + cx] += w * dt * 0.35f;
            }
        }

        // population control: smooth "mood" between sparse and dense scenes
        float target = TargetCount(time);
        int live = 0; foreach (var o in orgs) if (time - o.Birth < o.Life - o.FadeOut) live++;
        if (time >= nextSpawn && live < target) { Spawn(); nextSpawn = time + R(1.5f, 4.5f); }
        else if (live > target + 1.5f)
        {
            Org? worst = null;
            foreach (var o in orgs) if (time - o.Birth < o.Life - o.FadeOut - 1f && time - o.Birth > o.FadeIn + 5f && (worst == null || o.Life - (time - o.Birth) > worst.Life - (time - worst.Birth))) worst = o;
            if (worst != null) worst.Life = (time - worst.Birth) + worst.FadeOut + 0.5f;
        }

        // occasional events: merge two forms, or split one into a child
        if (time >= nextEvent && orgs.Count >= 3)
        {
            nextEvent = time + R(22f, 55f);
            if (F() < 0.5f)
            {
                var a = orgs[rng.Next(orgs.Count)];
                Org? b = null; float bd = float.MaxValue;
                foreach (var o in orgs) if (o != a && o.MergeInto == null) { float d = (o.X - a.X) * (o.X - a.X) + (o.Y - a.Y) * (o.Y - a.Y); if (d < bd) { bd = d; b = o; } }
                if (b != null && a.MergeInto == null && time - a.Birth > a.FadeIn) { a.MergeInto = b; a.Life = (time - a.Birth) + 14f + a.FadeOut; }
            }
            else
            {
                var p = orgs[rng.Next(orgs.Count)];
                if (time - p.Birth > p.FadeIn) Spawn(parent: p);
            }
        }

        Pack();
    }

    void Pack()
    {
        int n = 0;
        foreach (var o in orgs)
        {
            if (n >= Max) break;
            if (o.Weight < 0.002f) continue;
            int i = n * FloatsPerOrigin;
            Gpu[i + 0] = o.X; Gpu[i + 1] = o.Y; Gpu[i + 2] = o.RadiusNow; Gpu[i + 3] = o.Weight;
            Gpu[i + 4] = o.Rot; Gpu[i + 5] = o.Sym; Gpu[i + 6] = o.Stretch; Gpu[i + 7] = o.KindNow;
            float chroma = o.Chroma * set.ColorRichness / 100f;
            Gpu[i + 8] = o.Hue; Gpu[i + 9] = chroma; Gpu[i + 10] = o.Light; Gpu[i + 11] = o.Spread;
            Gpu[i + 12] = o.Radial; Gpu[i + 13] = o.TwistNow; Gpu[i + 14] = o.Density; Gpu[i + 15] = 0;
            Gpu[i + 16] = o.GradX; Gpu[i + 17] = o.GradY; Gpu[i + 18] = o.Freq; Gpu[i + 19] = o.Phase;
            var (r, g, b) = ColorMath.OklchToLinearSrgb(o.Light * 0.9f, chroma * 0.8f, o.Hue);
            Gpu[i + 20] = r * o.Weight; Gpu[i + 21] = g * o.Weight; Gpu[i + 22] = b * o.Weight; Gpu[i + 23] = 0;
            n++;
        }
        Count = n;
    }
}
