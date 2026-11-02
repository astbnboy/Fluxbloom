namespace Fluxbloom;

/// <summary>Kinds of form. Values match the HLSL type codes (Origin.d.w).</summary>
public enum FormType
{
    Flow = 0,
    Lissajous = 1, Spirograph = 2, Lorenz = 3, Superformula2D = 4, Superformula3D = 5, KleinBottle = 6,
    Mandelbulb = 10, QuaternionJulia = 11, Gyroid = 12, Julia2D = 13, Voronoi = 14,
}

/// <summary>
/// CPU-side composition director. It owns a pool of up to 24 "origins" (independent forms). Each is either a flow form or a
/// mathematical geometry form (see <see cref="FormType"/>), with its own position anywhere on the 32:9 canvas, scale, colour
/// (continuous OKLCH), lifecycle with long smooth fades, and continuously animated mathematical parameters. A coverage grid
/// steers new forms toward the least-covered part of the canvas so there are no persistent dead zones. It emits a flat float
/// array that mirrors the HLSL Origin struct (32 floats each).
/// </summary>
public sealed class Director
{
    public const int Max = 24;
    public const int FloatsPerOrigin = 32;
    const int GX = 64, GY = 16;

    /// <summary>Debug/test: restrict spawned forms to these type codes (set from FLX_TYPES in Program).</summary>
    public static int[]? DebugTypes;

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
        // geometry
        public int Slot;                   // fixed GPU slot for the form's whole life, so particle -> form indices stay valid
        public FormType Type;
        public float CurveFrac;
        public float[] B = new float[8];   // per-type base values
        public float[] W = new float[8];   // per-type phases
        public readonly float[] G = new float[4], H = new float[4];
    }

    readonly Random rng;
    readonly Settings set;
    readonly float aspect;
    readonly List<Org> orgs = new();
    readonly float[] cover = new float[GX * GY];
    float time, nextSpawn = 0.5f, nextEvent = 28f, nextGeoSpawn = 1f, nextGeoRoll;
    int geoTarget;
    FormType lastGeo = FormType.Flow;
    float globalHue;
    public readonly float[] Gpu = new float[Max * FloatsPerOrigin];
    public int Count { get; private set; }
    public int GeometryCount { get; private set; }
    /// <summary>Geometry forms currently visible (weight > 0.02). Never more than 3.</summary>
    public int VisibleGeometry { get; private set; }
    /// <summary>Sum of flow-particle weights (weight x density) and of curve-particle weights over live forms; the renderer sizes its particle pools from these.</summary>
    public float FlowWeightSum { get; private set; }
    public float CurveWeightSum { get; private set; }
    public float GlobalFade { get; private set; }
    public float Time => time;
    public int ActiveForms => orgs.Count;
    public string DebugState() => $"t={time:F1} geoTarget={geoTarget} nextGeoSpawn={nextGeoSpawn:F1} | " + string.Join("; ", orgs.Where(o => o.Type != FormType.Flow).Select(o => $"{o.Type} w={o.Weight:F3} age={time - o.Birth:F1}/{o.Life:F1} fadeIn={o.FadeIn:F1} fadeOut={o.FadeOut:F1}"));

    // audio features (0 when no music is playing); consumed by the animation, never used to drive time
    public float Bass, Mid, Treble, Beat;

    public Director(Settings s, float aspect, int seed)
    {
        set = s; this.aspect = aspect;
        rng = seed != 0 ? new Random(seed) : new Random();
        globalHue = (float)(rng.NextDouble() * Math.PI * 2);
        geoTarget = RollGeoTarget();
        nextGeoRoll = R(30f, 80f);
        // flow forms are the calm background; geometry is limited to 1-3 random forms at a time
        int nFlow = FlowTarget(0f) <= 0f ? 0 : Math.Max(3, (int)MathF.Round(TargetCount(0f)));
        for (int i = 0; i < nFlow; i++) Spawn(initial: true, force: FormType.Flow);
        for (int i = 0; i < geoTarget; i++) Spawn(initial: true, force: NextGeoType());
    }

    bool GeometryOnlyStyle => set.Style == 5 || set.Style == 6;
    /// <summary>Most geometry forms visible at once, counting a cross-fading pair: the setting (max 3), plus one only while swapping when below 3.</summary>
    int GeoVisibleCap { get { int m = Math.Clamp(set.MaxGeometry, 0, 3); return m >= 3 ? 3 : m + 1; } }
    float FlowTarget(float t) => GeometryOnlyStyle ? 0f : TargetCount(t);

    /// <summary>How many geometry forms to show for the next while: random 1..MaxGeometry (never more than 3, 0 if disabled).</summary>
    int RollGeoTarget()
    {
        int max = Math.Clamp(set.MaxGeometry, 0, 3);
        if (max == 0) return 0;
        return 1 + rng.Next(max);
    }

    /// <summary>Random geometry type that is not already on screen and not the one just shown.</summary>
    FormType NextGeoType()
    {
        if (DebugTypes is { Length: > 0 }) return (FormType)DebugTypes[rng.Next(DebugTypes.Length)];
        FormType[] all = { FormType.Mandelbulb, FormType.QuaternionJulia, FormType.Gyroid, FormType.Julia2D, FormType.Voronoi,
                           FormType.Lissajous, FormType.Spirograph, FormType.Lorenz, FormType.Superformula2D, FormType.Superformula3D, FormType.KleinBottle };
        FormType[] fractals = { FormType.Mandelbulb, FormType.QuaternionJulia, FormType.Julia2D };
        var pool = set.Style == 6 ? fractals : all;
        int rays = orgs.Count(o => IsRay(o.Type));
        var cand = pool.Where(tp => tp != lastGeo && !orgs.Any(o => o.Type == tp) && !(IsRay(tp) && rays >= 2)).ToArray();
        if (cand.Length == 0) cand = pool.Where(tp => tp != lastGeo).ToArray();
        var pick = cand[rng.Next(cand.Length)];
        lastGeo = pick;
        return pick;
    }

    float F() => (float)rng.NextDouble();
    float R(float a, float b) => a + (b - a) * F();

    float TargetCount(float t)
    {
        float m = 0.5f + 0.5f * MathF.Sin(t * 0.0261f + 0.7f) * MathF.Sin(t * 0.0113f + 2.1f + globalHue);
        m = ColorMath.Smooth(0.0f, 1.0f, m);
        float dens = set.Density / 100f;
        return Math.Clamp((2f + 6f * m) * dens, 1f, 12f);   // flow forms are only the backdrop now: about 2-8 at 100 % density
    }

    float PickKind()
    {
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

    static bool IsRay(FormType t) => t == FormType.Mandelbulb || t == FormType.QuaternionJulia || t == FormType.Gyroid;
    static bool IsGeometry(FormType t) => t != FormType.Flow;

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
        if (F() < 0.14f)
        {
            if (F() < 0.6f) bx = (F() < 0.5f ? -1f : 1f) * aspect * R(0.88f, 1.08f);
            if (F() < 0.6f) by = (F() < 0.5f ? -1f : 1f) * R(0.82f, 1.08f);
        }
        return (bx, by);
    }

    void Spawn(bool initial = false, Org? parent = null, FormType? force = null)
    {
        if (orgs.Count >= Max) return;
        var o = new Org();
        o.Type = force ?? FormType.Flow;   // geometry forms are only ever created by the 1-3 slot scheduler (NextGeoType)
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
        for (int i = 0; i < 8; i++) o.W[i] = R(0, 6.2832f);
        ShapeGeometry(o);
        var used = new bool[Max]; foreach (var q in orgs) used[q.Slot] = true;
        int slot = 0; while (slot < Max - 1 && used[slot]) slot++;
        o.Slot = slot;
        orgs.Add(o);
    }

    /// <summary>Type-specific size, density and base parameters.</summary>
    void ShapeGeometry(Org o)
    {
        if (!IsGeometry(o.Type)) { o.CurveFrac = 0; return; }
        switch (o.Type)
        {
            case FormType.Lissajous:
                o.Radius = R(0.45f, 0.9f); o.Stretch = R(1.0f, 2.4f); o.CurveFrac = R(0.8f, 1.2f); o.Density = R(0.15f, 0.3f);
                o.B[0] = new[] { 2f, 3f, 4f, 5f }[rng.Next(4)]; o.B[1] = new[] { 3f, 4f, 5f, 7f }[rng.Next(4)]; o.B[2] = new[] { 2f, 3f, 5f }[rng.Next(3)];
                break;
            case FormType.Spirograph:
                o.Radius = R(0.45f, 0.85f); o.Stretch = R(1.0f, 1.8f); o.CurveFrac = R(0.8f, 1.2f); o.Density = R(0.15f, 0.3f);
                o.B[0] = R(2.3f, 7.7f); o.B[1] = R(0.5f, 1.2f);
                break;
            case FormType.Lorenz:
                o.Radius = R(0.5f, 0.95f); o.Stretch = R(1.0f, 1.8f); o.CurveFrac = R(1.4f, 2.0f); o.Density = R(0.15f, 0.3f);
                o.B[0] = R(26f, 31f); o.B[1] = R(1.0f, 1.5f);
                break;
            case FormType.Superformula2D:
                o.Radius = R(0.4f, 0.8f); o.Stretch = R(1.0f, 1.6f); o.CurveFrac = R(0.8f, 1.2f); o.Density = R(0.15f, 0.3f);
                o.B[0] = rng.Next(2, 9);
                break;
            case FormType.Superformula3D:
                o.Radius = R(0.45f, 0.85f); o.Stretch = 1f; o.CurveFrac = R(1.2f, 1.8f); o.Density = R(0.15f, 0.3f);
                o.B[0] = rng.Next(2, 9); o.B[1] = rng.Next(2, 7);
                break;
            case FormType.KleinBottle:
                o.Radius = R(0.45f, 0.8f); o.Stretch = 1f; o.CurveFrac = R(1.2f, 1.8f); o.Density = R(0.15f, 0.3f);
                o.B[0] = R(0.6f, 1.2f);
                break;
            case FormType.Mandelbulb: case FormType.QuaternionJulia: case FormType.Gyroid:
                o.Radius = R(0.42f, 0.78f); o.Stretch = 1f; o.CurveFrac = 0; o.Density = R(0.25f, 0.5f); o.Rot = 0;
                break;
            case FormType.Julia2D: case FormType.Voronoi:
                o.Radius = R(0.45f, 0.85f); o.Stretch = 1f; o.CurveFrac = 0; o.Density = R(0.25f, 0.5f);
                break;
        }
        o.Life = R(60f, 130f);
        for (int tries = 0; tries < 6; tries++)   // stagger: never end within 35 s of another geometry form, so replacements overlap smoothly
        {
            bool clash = false;
            foreach (var q in orgs) if (q.Type != FormType.Flow && MathF.Abs((q.Birth + q.Life) - (time + o.Life)) < 35f) { clash = true; break; }
            if (!clash) break;
            o.Life += 20f;
        }
        o.Radius = MathF.Min(o.Radius * 1.45f, 1.05f);   // few forms on screen, so let each one be a feature
        if (IsRay(o.Type) || o.Type == FormType.Julia2D || o.Type == FormType.Voronoi) { o.Sym = 1; }
    }

    static float S(float t, float w, float ph) => MathF.Sin(t * w + ph);

    /// <summary>Continuous parameter evolution for the form's mathematics (all smooth; nothing ever jumps).</summary>
    void Animate(Org o)
    {
        float t = time;
        var G = o.G; var H = o.H;
        Array.Clear(G); Array.Clear(H);
        switch (o.Type)
        {
            case FormType.Lissajous:
                G[0] = o.B[0] + 0.35f * S(t, 0.071f, o.W[0]);
                G[1] = o.B[1] + 0.35f * S(t, 0.053f, o.W[1]);
                G[2] = o.B[2] + 0.30f * S(t, 0.043f, o.W[2]);
                G[3] = t * 0.21f + o.W[3];
                H[0] = 1.0f;                                               // one period: a clean closed figure
                break;
            case FormType.Spirograph:
                G[0] = o.B[0] + 0.6f * S(t, 0.041f, o.W[0]);          // R/r ratio
                G[1] = Math.Clamp(o.B[1] + 0.35f * S(t, 0.057f, o.W[1]), 0.15f, 1.5f);   // pen offset
                G[2] = S(t, 0.031f, o.W[2]);                           // hypotrochoid (+1) <-> epitrochoid (-1)
                G[3] = 7f;
                break;
            case FormType.Lorenz:
                G[0] = o.B[0] + 4f * S(t, 0.047f, o.W[0]);             // rho
                G[1] = o.B[1];
                break;
            case FormType.Superformula2D:
                G[0] = o.B[0] + 2.2f * (0.5f + 0.5f * S(t, 0.033f, o.W[0]));   // m (fractional, morphs between symmetries)
                G[1] = 0.8f + 0.5f * S(t, 0.041f, o.W[1]);
                G[2] = 2.2f + 1.4f * S(t, 0.037f, o.W[2]);
                G[3] = 2.2f + 1.4f * S(t, 0.045f, o.W[3]);
                break;
            case FormType.Superformula3D:
                G[0] = o.B[0] + 2.0f * (0.5f + 0.5f * S(t, 0.031f, o.W[0]));
                G[1] = 0.9f + 0.4f * S(t, 0.039f, o.W[1]);
                G[2] = 2.4f + 1.2f * S(t, 0.035f, o.W[2]);
                G[3] = 2.4f + 1.2f * S(t, 0.043f, o.W[3]);
                H[0] = o.B[1] + 1.6f * (0.5f + 0.5f * S(t, 0.029f, o.W[4]));
                H[1] = 0.9f + 0.4f * S(t, 0.047f, o.W[5]);
                H[2] = 2.2f + 1.2f * S(t, 0.033f, o.W[6]);
                H[3] = 2.2f + 1.2f * S(t, 0.041f, o.W[7]);
                break;
            case FormType.KleinBottle:
                G[0] = 1.25f; G[1] = 0.55f + 0.08f * S(t, 0.05f, o.W[0]); G[2] = o.B[0];
                break;
            case FormType.Mandelbulb:
                G[0] = 8f + 2.4f * S(t, 0.037f, o.W[0]) + 1.2f * Mid;  // power
                break;
            case FormType.QuaternionJulia:
                G[0] = -0.2f + 0.25f * S(t, 0.031f, o.W[0]); G[1] = 0.8f + 0.12f * S(t, 0.027f, o.W[1]);
                G[2] = 0.1f + 0.25f * S(t, 0.019f, o.W[2]); G[3] = 0.10f * S(t, 0.023f, o.W[3]);
                H[0] = 0.15f * S(t, 0.041f, o.W[4]);                       // 4D slice position
                break;
            case FormType.Gyroid:
                G[0] = 3.2f + 1.5f * (0.5f + 0.5f * S(t, 0.028f, o.W[0]));
                G[1] = 0.12f + 0.07f * S(t, 0.045f, o.W[1]) + 0.03f * Treble;
                G[2] = 0.5f + 0.5f * S(t, 0.03f, o.W[2]);                  // gyroid <-> Schwarz-P
                break;
            case FormType.Julia2D:
                {
                    float a = t * 0.05f + o.W[0], r = 0.7885f;
                    G[0] = r * MathF.Cos(a); G[1] = r * MathF.Sin(a);       // c travels the Julia "lotus" circle
                    G[2] = 1.55f + 0.2f * S(t, 0.03f, o.W[1]);
                }
                break;
            case FormType.Voronoi:
                G[0] = 7f + 2.5f * S(t, 0.037f, o.W[0]);
                G[1] = 0.8f + 0.75f * S(t, 0.06f, o.W[1]);                 // continuous subdivision level
                G[2] = 0.05f; G[3] = 0.4f;
                break;
        }
    }

    public void Update(float dt)
    {
        time += dt;
        float sp = set.Speed / 100f;
        globalHue += dt * 0.0026f * (set.ColorCycle / 100f);
        GlobalFade = ColorMath.Smooth(0f, 3.5f, time);

        float decay = MathF.Exp(-dt / 22f);
        for (int i = 0; i < cover.Length; i++) cover[i] *= decay;

        for (int i = orgs.Count - 1; i >= 0; i--)
        {
            var o = orgs[i];
            float age = time - o.Birth, end = o.Life;
            float w = ColorMath.Smooth(0f, o.FadeIn, age) * (1f - ColorMath.Smooth(end - o.FadeOut, end, age));
            o.Weight = w;
            if (age > end) { orgs.RemoveAt(i); continue; }

            o.X += o.VX * dt; o.Y += o.VY * dt;
            float ox = MathF.Abs(o.X) - aspect * 1.15f, oy = MathF.Abs(o.Y) - 1.15f;
            if (ox > 0) o.VX -= MathF.Sign(o.X) * ox * 0.02f * dt;
            if (oy > 0) o.VY -= MathF.Sign(o.Y) * oy * 0.02f * dt;
            o.Rot += o.RotVel * dt;
            o.Hue += o.HueRate * dt * (set.ColorCycle / 100f);
            o.KindNow = Math.Clamp(o.Kind0 + o.KindAmp * MathF.Sin(time * o.KindW + o.KindPh), 0f, 4f);
            o.RadiusNow = o.Radius * (1f + 0.16f * MathF.Sin(time * 0.071f + o.Ph1) + 0.07f * MathF.Sin(time * 0.173f + o.Ph2)) * (1f + 0.06f * Bass);
            o.TwistNow = o.Twist * (0.75f + 0.25f * MathF.Sin(time * 0.05f + o.Ph3));
            Animate(o);

            if (o.MergeInto != null)
            {
                var t = o.MergeInto;
                o.X += (t.X - o.X) * 0.12f * dt; o.Y += (t.Y - o.Y) * 0.12f * dt;
                float dh = t.Hue - o.Hue; o.Hue += dh * 0.05f * dt;
                t.Hue -= dh * 0.01f * dt;
            }

            if (w > 0.02f)
            {
                float rx = o.RadiusNow * MathF.Max(o.Stretch, 1f) * 1.2f, ry = o.RadiusNow * 1.2f;
                int x0 = Math.Max(0, (int)((o.X - rx) / aspect * 0.5f * GX + GX * 0.5f)), x1 = Math.Min(GX - 1, (int)((o.X + rx) / aspect * 0.5f * GX + GX * 0.5f));
                int y0 = Math.Max(0, (int)((1f - (o.Y + ry)) * 0.5f * GY)), y1 = Math.Min(GY - 1, (int)((1f - (o.Y - ry)) * 0.5f * GY));
                for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++) cover[cy * GX + cx] += w * dt * 0.35f;
            }
        }

        // ---- flow forms: the calm background engine (4 of the 24 slots stay reserved for geometry)
        float target = FlowTarget(time);
        int live = 0, liveGeo = 0;
        foreach (var o in orgs)
        {
            bool alive = time - o.Birth < o.Life - o.FadeOut;
            if (!alive) continue;
            if (o.Type == FormType.Flow) live++; else liveGeo++;
        }
        if (time >= nextSpawn && live < target && orgs.Count < Max - 4) { Spawn(force: FormType.Flow); nextSpawn = time + R(1.5f, 4.5f); }
        else if (live > target + 1.5f)
        {
            Org? worst = null;
            foreach (var o in orgs) if (o.Type == FormType.Flow && time - o.Birth < o.Life - o.FadeOut - 1f && time - o.Birth > o.FadeIn + 5f && (worst == null || o.Life - (time - o.Birth) > worst.Life - (time - worst.Birth))) worst = o;
            if (worst != null) worst.Life = (time - worst.Birth) + worst.FadeOut + 0.5f;
        }

        // ---- geometry: only 1 to 3 random forms on screen at any time, cross-fading
        if (time >= nextGeoRoll) { geoTarget = RollGeoTarget(); nextGeoRoll = time + R(30f, 80f); }
        int visGeo = 0, rising = 0;   // forms that are visible (weight > 0.02), and forms just spawned that will become visible
        foreach (var o in orgs) { if (o.Type == FormType.Flow) continue; if (o.Weight > 0.02f) visGeo++; else if (time - o.Birth < o.FadeIn) rising++; }
        if (liveGeo > geoTarget)
        {
            // too many: retire the one that has been up longest (it fades out over 10-22 s) - but one at a time, and never while another
            // geometry form is fading or about to, so the screen is never left without geometry
            bool anyFading = false; float nextFade = float.MaxValue; Org? oldest = null;
            foreach (var o in orgs)
            {
                if (o.Type == FormType.Flow) continue;
                float fadeStartIn = (o.Birth + o.Life - o.FadeOut) - time;
                if (fadeStartIn <= 0f) { anyFading = true; continue; }
                nextFade = MathF.Min(nextFade, fadeStartIn);
                if (oldest == null || o.Birth < oldest.Birth) oldest = o;
            }
            if (!anyFading && nextFade > 25f && oldest != null) oldest.Life = (time - oldest.Birth) + oldest.FadeOut + 0.5f;
        }
        else if (liveGeo < geoTarget && visGeo + rising < GeoVisibleCap && (liveGeo == 0 || time >= nextGeoSpawn))   // never leave the screen without geometry
        {
            Spawn(force: NextGeoType());
            nextGeoSpawn = time + R(2f, 5f);
        }

        // ---- events on flow forms only: merge two, or split one into a child
        if (time >= nextEvent)
        {
            var flows = orgs.Where(o => o.Type == FormType.Flow).ToList();
            if (flows.Count >= 3)
            {
                nextEvent = time + R(22f, 55f);
                if (F() < 0.5f)
                {
                    var a = flows[rng.Next(flows.Count)];
                    Org? b = null; float bd = float.MaxValue;
                    foreach (var o in flows) if (o != a && o.MergeInto == null) { float d = (o.X - a.X) * (o.X - a.X) + (o.Y - a.Y) * (o.Y - a.Y); if (d < bd) { bd = d; b = o; } }
                    if (b != null && a.MergeInto == null && time - a.Birth > a.FadeIn) { a.MergeInto = b; a.Life = (time - a.Birth) + 14f + a.FadeOut; }
                }
                else
                {
                    var p = flows[rng.Next(flows.Count)];
                    if (time - p.Birth > p.FadeIn && orgs.Count < Max - 4) Spawn(parent: p, force: FormType.Flow);
                }
            }
        }

        Pack();
    }

    void Pack()
    {
        Array.Clear(Gpu);
        int n = 0, geo = 0, top = 0; float fw = 0, cw = 0;
        foreach (var o in orgs)
        {
            if (o.Weight < 0.002f) continue;
            int i = o.Slot * FloatsPerOrigin;
            top = Math.Max(top, o.Slot + 1);
            Gpu[i + 0] = o.X; Gpu[i + 1] = o.Y; Gpu[i + 2] = o.RadiusNow; Gpu[i + 3] = o.Weight;
            Gpu[i + 4] = o.Rot; Gpu[i + 5] = o.Sym; Gpu[i + 6] = o.Stretch; Gpu[i + 7] = o.KindNow;
            float chroma = o.Chroma * set.ColorRichness / 100f;
            Gpu[i + 8] = o.Hue; Gpu[i + 9] = chroma; Gpu[i + 10] = o.Light; Gpu[i + 11] = o.Spread;
            Gpu[i + 12] = o.Radial; Gpu[i + 13] = o.TwistNow; Gpu[i + 14] = o.Density; Gpu[i + 15] = (float)o.Type;
            Gpu[i + 16] = o.GradX; Gpu[i + 17] = o.GradY; Gpu[i + 18] = o.Freq; Gpu[i + 19] = o.Phase;
            var (r, g, b) = ColorMath.OklchToLinearSrgb(o.Light * 0.9f, chroma * 0.8f, o.Hue);
            Gpu[i + 20] = r * o.Weight; Gpu[i + 21] = g * o.Weight; Gpu[i + 22] = b * o.Weight; Gpu[i + 23] = o.CurveFrac;
            for (int k = 0; k < 4; k++) { Gpu[i + 24 + k] = o.G[k]; Gpu[i + 28 + k] = o.H[k]; }
            if (IsGeometry(o.Type)) geo++;
            fw += o.Weight * o.Density;
            if (o.Type >= FormType.Lissajous && o.Type <= FormType.KleinBottle) cw += o.Weight * o.CurveFrac;
            n++;
        }
        VisibleGeometry = 0; foreach (var o in orgs) if (o.Type != FormType.Flow && o.Weight > 0.02f) VisibleGeometry++;
        Count = top; GeometryCount = geo; FlowWeightSum = fw; CurveWeightSum = cw;   // Count = highest used slot + 1 (shader loop bound)
    }
}
