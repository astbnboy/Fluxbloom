// Fluxbloom - GPU generative screensaver. All stages in one file (SM 5.1, compiled at startup).
// Canvas space: y in [-1,1] (up), x in [-gAspect, gAspect]. Colours are authored in OKLCH and carried as
// continuous floating point linear light until the final 10-bit (or HDR10) dithered encode.

#define MAX_ORIGINS 24
#define PI 3.14159265359
#define TAU 6.28318530718

struct Origin
{
    float4 a; // pos.xy, radius, weight (0..1 lifecycle fade)
    float4 b; // rotation, symmetry (N-fold, 1 = none), stretch, kind (0..4 continuous)
    float4 c; // hue (rad), chroma, lightness, hueSpread
    float4 d; // radial flow (signed: in/out), twist, density, unused
    float4 e; // hueGradient.xy (rad per canvas unit), freq, seed phase
    float4 f; // haze tint (linear rgb, premultiplied), unused
};

cbuffer Frame : register(b0)
{
    float2 gRes;  float2 gInvRes;
    float gAspect; float gTime; float gDt; uint gFrame;
    float gExposure; float gBloom; float gDecay; float gInject;
    uint gParticleCount; uint gOriginCount; uint gGamutMode; uint gOutMode;
    float gPaperWhite; float gHud; float gFlowSpeed; float gDither;
    float gHaze; float gDetail; float gParticleSize; float gBrightness;
    float gAdvect; float gSoftFrac; float gPad0; float gPad1;
    float4 gSeed;
    float2 gFlowTexSize; float2 gHudSize;
    Origin gO[MAX_ORIGINS];
};

cbuffer PassData : register(b1)
{
    float4 gPass; // x,y = source texel size, z = weight, w = mode
};

struct Particle
{
    float2 pos; float2 prev;
    float age; float life; float hseed; uint origin;
    float2 vel; float rnd; uint pad;
};

Texture2D<float4> gTex0 : register(t0);
Texture2D<float4> gTex1 : register(t1);
StructuredBuffer<Particle> gPS : register(t2);
Texture2D<float4> gHudTex : register(t3);
Texture2D<float2> gFlowTex : register(t4);
RWStructuredBuffer<Particle> gP : register(u0);
RWTexture2D<float2> gFlowOut : register(u1);
SamplerState sLin : register(s0);

// ------------------------------------------------------------------ utilities
uint pcg(uint v)
{
    uint s = v * 747796405u + 2891336453u;
    uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}
float rf(inout uint s) { s = pcg(s); return (s >> 8) * (1.0 / 16777216.0); }

float hashLat(int2 p, uint salt)
{
    uint h = pcg(asuint(p.x) + pcg(asuint(p.y) + salt));
    return (h >> 8) * (1.0 / 16777216.0);
}
float2 hashLat2(int2 p, uint salt)
{
    return float2(hashLat(p, salt), hashLat(p, salt + 7919u));
}

float vnoise(float2 p, uint salt)
{
    int2 i = (int2)floor(p);
    float2 f = frac(p);
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = hashLat(i, salt);
    float b = hashLat(i + int2(1, 0), salt);
    float c = hashLat(i + int2(0, 1), salt);
    float d = hashLat(i + int2(1, 1), salt);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y) * 2.0 - 1.0;
}

float fbm(float2 p, uint salt)
{
    float s = 0, a = 0.5;
    [unroll] for (int i = 0; i < 3; i++) { s += a * vnoise(p, salt + i * 31u); p = p * 2.03 + 11.7; a *= 0.5; }
    return s;
}

float2 curlNoise(float2 p, float t, uint salt)
{
    float e = 0.02;
    float2 q = p + float2(t * 0.045, -t * 0.037);
    float n1 = fbm(q + float2(0, e), salt), n2 = fbm(q - float2(0, e), salt);
    float n3 = fbm(q + float2(e, 0), salt), n4 = fbm(q - float2(e, 0), salt);
    return float2(n1 - n2, -(n3 - n4)) / (2.0 * e);
}

float2 cellFlow(float2 p, uint salt)
{
    int2 ip = (int2)floor(p);
    float2 fp = frac(p);
    float d1 = 8, d2 = 8; float2 c1 = 0, c2 = 0;
    [unroll] for (int j = -1; j <= 1; j++)
    [unroll] for (int i = -1; i <= 1; i++)
    {
        float2 g = float2(i, j);
        float2 o = hashLat2(ip + int2(i, j), salt);
        float2 r = g + o - fp;
        float d = dot(r, r);
        if (d < d1) { d2 = d1; c2 = c1; d1 = d; c1 = r; }
        else if (d < d2) { d2 = d; c2 = r; }
    }
    float2 n = normalize(c2 - c1 + 1e-5);
    float2 tang = float2(-n.y, n.x);
    float edge = exp(-(sqrt(d2) - sqrt(d1)) * 5.0);
    return tang * edge * 1.4 - normalize(c1 + 1e-5) * (1.0 - edge) * 0.55;
}

// ------------------------------------------------------------------ colour
float3 oklab_to_lrgb(float3 c)
{
    float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
    return float3(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                 -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                 -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}

static const float3x3 M709to2020 = { 0.627404, 0.329283, 0.043313, 0.069097, 0.919540, 0.011362, 0.016391, 0.088013, 0.895595 };
static const float3x3 M2020to709 = { 1.660491, -0.587641, -0.072850, -0.124550, 1.132900, -0.008349, -0.018151, -0.100579, 1.118730 };

float3 desatIntoGamut(float3 c, float3 lw)
{
    float Y = max(dot(c, lw), 0.0);
    float mn = min(c.r, min(c.g, c.b));
    if (mn < 0.0) { float k = Y / (Y - mn); c = Y + (c - Y) * k; }
    return c;
}

// mode 0: clip to sRGB gamut (desaturate toward grey, keeps hue). mode 1: clip to Rec.2020 (wide gamut / HDR10 path)
float3 GamutMap(float3 rgb709, uint mode)
{
    if (mode == 0) return max(desatIntoGamut(rgb709, float3(0.2126, 0.7152, 0.0722)), 0.0);
    float3 c = mul(M709to2020, rgb709);
    c = desatIntoGamut(c, float3(0.2627, 0.6780, 0.0593));
    return mul(M2020to709, max(c, 0.0));
}

float3 LCH(float L, float C, float h)
{
    float s, c; sincos(h, s, c);
    return GamutMap(oklab_to_lrgb(float3(L, C * c, C * s)), gGamutMode);
}

float3 Tonemap(float3 c)
{
    c *= gBrightness * gExposure;
    float3 a = 1.0 - exp(-c);
    float Y = dot(c, float3(0.2126, 0.7152, 0.0722));
    float Yt = 1.0 - exp(-Y);
    float3 b = c * (Y > 1e-5 ? Yt / Y : 1.0);
    return lerp(a, b, 0.55);
}

float3 LinearToSRGB(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float3 PQ(float3 L)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 Lp = pow(max(L, 0.0), m1);
    return pow((c1 + c2 * Lp) / (1.0 + c3 * Lp), m2);
}

// ------------------------------------------------------------------ canvas mapping
float2 UvToCanvas(float2 uv) { return float2((uv.x * 2.0 - 1.0) * gAspect, 1.0 - uv.y * 2.0); }
float2 CanvasToUv(float2 p) { return float2(p.x / gAspect * 0.5 + 0.5, 0.5 - p.y * 0.5); }
float2 CanvasToPx(float2 p) { return CanvasToUv(p) * gRes; }

// ------------------------------------------------------------------ flow
float2 rot2(float2 v, float a) { float s, c; sincos(a, s, c); return float2(c * v.x - s * v.y, s * v.x + c * v.y); }

// Velocity contributed by one origin at world position p (canvas units / second before speed scaling).
float2 OriginFlow(Origin o, float2 p, float t, float detail)
{
    float2 q = p - o.a.xy;
    q = rot2(q, -o.b.x);
    float stretch = max(o.b.z, 0.2);
    q.x /= stretch;
    float R = max(o.a.z, 0.05);
    float2 qn = q / R;
    float r2 = dot(qn, qn);
    float env = exp(-r2 * 0.7) + 0.22 / (1.0 + r2 * 0.5);
    if (env * o.a.w < 0.004) return 0;

    float N = max(o.b.y, 1.0);
    float r = sqrt(r2);
    float sector = TAU / N;
    float k = 0; bool refl = false;
    float2 qf = qn;
    if (N >= 1.5)
    {
        float th = atan2(qn.y, qn.x); if (th < 0) th += TAU;
        k = floor(th / sector);
        float la = th - k * sector;
        refl = la > 0.5 * sector;
        if (refl) la = sector - la;
        qf = r * float2(cos(la), sin(la));
    }

    float kind = o.b.w;
    float w0 = saturate(1.0 - abs(kind - 0.0));
    float w1 = saturate(1.0 - abs(kind - 1.0));
    float w2 = saturate(1.0 - abs(kind - 2.0));
    float w3 = saturate(1.0 - abs(kind - 3.0));
    float w4 = saturate(1.0 - abs(kind - 4.0));
    float tw = o.d.y, rad = o.d.x, freq = o.e.z, ph = o.e.w;
    uint salt = (uint)(ph * 1000.0);
    float2 f = 0;

    if (w0 > 0) { float2 perp = float2(-qf.y, qf.x); f += w0 * (tw * perp - rad * qf) / (r + 0.25); }
    if (w1 > 0) { f += w1 * float2(sin(qf.y * freq * 1.7 + t * 0.31 + ph), sin(qf.x * freq * 1.3 - t * 0.27 + ph * 1.7)) * 0.9; }
    if (w2 > 0)
    {
        float2 z = qf * 1.4;
        float2 cc = 0.5 * float2(cos(t * 0.05 + ph), sin(t * 0.07 + 1.3 * ph));
        float2 z2 = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + cc;
        float2 dz = z2 - z;
        f += w2 * (dz / (1.0 + length(dz))) * 1.2;
    }
    if (w3 > 0) { f += w3 * (curlNoise(qf * freq * 0.8 + ph * 7.0, t, salt) * 1.3 + 0.25 * tw * float2(-qf.y, qf.x) / (r + 0.3)); }
    if (w4 > 0) { f += w4 * cellFlow(qf * freq * 0.9 + ph, salt + 5u); }
    // extra fine structure for the particle (detail) evaluation: a second octave of curl riding on the base flow
    if (detail > 0.5) { f += 0.35 * curlNoise(qf * freq * 2.6 + ph * 3.0, t * 1.3, salt + 91u); }

    if (N >= 1.5)
    {
        if (refl) f.y = -f.y;
        float ang = (refl ? (k + 1.0) : k) * sector;
        f = rot2(f, ang);
    }
    f.x *= stretch;
    f = rot2(f, o.b.x);
    return f * env * o.a.w;
}

// ------------------------------------------------------------------ compute: coarse flow field
[numthreads(8, 8, 1)]
void CS_Flow(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)gFlowTexSize.x || id.y >= (uint)gFlowTexSize.y) return;
    float2 uv = (id.xy + 0.5) / gFlowTexSize;
    float2 p = UvToCanvas(uv);
    float2 F = 0.30 * curlNoise(p * 0.55, gTime, 1234u);
    [loop] for (uint i = 0; i < gOriginCount; i++) F += OriginFlow(gO[i], p, gTime, 0.0);
    gFlowOut[id.xy] = F * 0.06;
}

// ------------------------------------------------------------------ compute: particles
void Respawn(inout Particle p, inout uint s, bool first)
{
    float W = 0;
    [loop] for (uint i = 0; i < gOriginCount; i++) W += gO[i].a.w * gO[i].d.z;
    if (W <= 1e-4) { p.life = 0; p.age = 0; p.pos = float2(99, 99); p.prev = p.pos; return; }
    float r = rf(s) * W, acc = 0; uint home = 0;
    [loop] for (uint j = 0; j < gOriginCount; j++) { acc += gO[j].a.w * gO[j].d.z; home = j; if (r <= acc) break; }
    Origin o = gO[home];
    float mode = rf(s);
    float2 pos;
    if (mode < 0.09)
    {
        pos = float2((rf(s) * 2.0 - 1.0) * gAspect * 1.03, (rf(s) * 2.0 - 1.0) * 1.03); // ambient: anywhere on the canvas
    }
    else
    {
        float ang = rf(s) * TAU;
        float rr = (mode < 0.55) ? 0.35 * sqrt(-log(max(rf(s), 1e-4))) : (0.4 + 1.8 * pow(rf(s), 1.6));
        float2 l = o.a.z * rr * float2(cos(ang), sin(ang));
        l.x *= max(o.b.z, 0.2);
        pos = o.a.xy + rot2(l, o.b.x);
    }
    p.life = 4.0 + 10.0 * rf(s);
    p.age = first ? rf(s) * p.life : 0.0;
    p.pos = pos; p.prev = pos; p.vel = 0;
    p.hseed = rf(s); p.origin = home; p.rnd = rf(s);
}

[numthreads(64, 1, 1)]
void CS_Update(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= gParticleCount) return;
    Particle p = gP[i];
    uint s = pcg(i * 9781u + gFrame * 6271u + asuint(gSeed.x));
    bool first = (p.life <= 0.0);
    float lim = 1.08;
    bool dead = first || p.age >= p.life || abs(p.pos.x) > gAspect * lim || abs(p.pos.y) > lim || p.origin >= gOriginCount;
    if (dead)
    {
        Respawn(p, s, first);
    }
    else
    {
        float dt = gDt;
        p.age += dt;
        float2 uv = CanvasToUv(p.pos);
        float2 v = gFlowTex.SampleLevel(sLin, uv, 0) * gFlowSpeed;
        Origin o = gO[p.origin];
        v += OriginFlow(o, p.pos, gTime, 1.0) * 0.06 * gFlowSpeed * gDetail;
        p.vel = lerp(p.vel, v, 1.0 - exp(-dt * 3.0));
        p.prev = p.pos;
        p.pos += p.vel * dt;
    }
    gP[i] = p;
}

// ------------------------------------------------------------------ fullscreen helpers
struct VSF { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSF VS_Full(uint id : SV_VertexID)
{
    VSF o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0, 1);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Feedback field: advect + decay the previous frame along the flow field and add a soft procedural haze from every origin.
float4 PS_Field(VSF i) : SV_Target
{
    float2 uv = i.uv;
    float2 p = UvToCanvas(uv);
    float2 v = gFlowTex.SampleLevel(sLin, uv, 0) * gAdvect;
    float2 uv2 = uv - float2(v.x * gDt / gAspect * 0.5, -v.y * gDt * 0.5);
    float3 h = gTex0.SampleLevel(sLin, uv2, 0).rgb * exp(-gDt / max(gDecay, 0.05));

    float3 haze = 0;
    [loop] for (uint k = 0; k < gOriginCount; k++)
    {
        Origin o = gO[k];
        float2 q = rot2(p - o.a.xy, -o.b.x);
        q.x /= max(o.b.z, 0.2);
        float R = max(o.a.z, 0.05);
        float r2 = dot(q, q) / (R * R);
        if (r2 > 9.0) continue;
        float r = sqrt(r2);
        float shells = 0.55 + 0.45 * cos(r * (3.0 + o.e.z) * 2.2 - gTime * 0.25 + o.e.w + 0.8 * sin(atan2(q.y, q.x) * max(o.b.y, 1.0) + gTime * 0.1));
        haze += o.f.rgb * (exp(-r2 * 0.9) * shells);
    }
    return float4(h + haze * gHaze * gDt, 1.0);
}

// ------------------------------------------------------------------ particles
struct VSP { float4 pos : SV_Position; nointerpolation float2 a : TEXCOORD0; nointerpolation float2 b : TEXCOORD1; float2 px : TEXCOORD2; nointerpolation float4 col : COLOR0; nointerpolation float rad : TEXCOORD3; };

VSP VS_Particle(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    VSP o;
    Particle p = gPS[iid];
    Origin og = gO[min(p.origin, MAX_ORIGINS - 1)];
    bool alive = (p.life > 0.0) && (p.origin < gOriginCount) && (og.a.w > 0.002);
    float fade = smoothstep(0.0, 1.0, p.age) * smoothstep(0.0, 1.5, p.life - p.age);

    float h = og.c.x + (p.hseed - 0.5) * og.c.w + dot(og.e.xy, p.pos) + 0.25 * sin(p.age * 0.6 + p.hseed * TAU);
    float C = og.c.y * (0.55 + 0.9 * frac(p.hseed * 13.37 + p.rnd * 3.1));
    float L = og.c.z + 0.10 * (p.rnd - 0.5);
    float3 col = LCH(L, C, h);

    bool big = p.rnd > 0.985;
    float rad = gParticleSize * (big ? 3.2 : 1.0);
    float gain = gInject * gDt * fade * og.a.w * (big ? 0.9 : 1.0) / max(rad, 0.5);
    o.col = float4(col * gain, 1);
    o.rad = rad;

    float2 a = CanvasToPx(p.prev), b = CanvasToPx(p.pos);
    float2 d = b - a; float len = length(d);
    float2 dir = len > 1e-4 ? d / len : float2(1, 0);
    float2 nrm = float2(-dir.y, dir.x);
    float ext = rad * 1.5 + 0.8;
    float s = (vid & 1) ? 1.0 : 0.0;
    float t = (vid & 2) ? 1.0 : -1.0;
    float2 px = lerp(a, b, s) + dir * (s > 0.5 ? ext : -ext) + nrm * t * ext;
    o.px = px; o.a = a; o.b = b;
    float2 ndc = px * gInvRes * 2.0 - 1.0; ndc.y = -ndc.y;
    o.pos = alive ? float4(ndc, 0, 1) : float4(2, 2, 2, 1);
    return o;
}

float4 PS_Particle(VSP i) : SV_Target
{
    float2 ab = i.b - i.a;
    float l2 = max(dot(ab, ab), 1e-6);
    float tt = saturate(dot(i.px - i.a, ab) / l2);
    float2 c = i.a + ab * tt;
    float d2 = dot(i.px - c, i.px - c);
    float r = i.rad;
    float w = exp(-d2 / (0.5 * r * r + 0.15));
    return float4(i.col.rgb * w, 0);
}

// ------------------------------------------------------------------ bloom
float4 PS_Down(VSF i) : SV_Target
{
    float2 ts = gPass.xy;
    float3 c = gTex0.SampleLevel(sLin, i.uv, 0).rgb * 0.5;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(-1, -1), 0).rgb * 0.125;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(1, -1), 0).rgb * 0.125;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(-1, 1), 0).rgb * 0.125;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(1, 1), 0).rgb * 0.125;
    return float4(min(c, 12.0), 1);
}

float4 PS_Up(VSF i) : SV_Target
{
    float2 ts = gPass.xy;
    float3 c = 0;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(-1, -1), 0).rgb;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(0, -1), 0).rgb * 2.0;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(1, -1), 0).rgb;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(-1, 0), 0).rgb * 2.0;
    c += gTex0.SampleLevel(sLin, i.uv, 0).rgb * 4.0;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(1, 0), 0).rgb * 2.0;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(-1, 1), 0).rgb;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(0, 1), 0).rgb * 2.0;
    c += gTex0.SampleLevel(sLin, i.uv + ts * float2(1, 1), 0).rgb;
    return float4(c * (1.0 / 16.0) * gPass.z, 0);
}

// ------------------------------------------------------------------ composite
float4 PS_Composite(VSF i) : SV_Target
{
    float3 c = gTex0.SampleLevel(sLin, i.uv, 0).rgb;
    c += gTex1.SampleLevel(sLin, i.uv, 0).rgb * gBloom;
    c = Tonemap(c);
    if (gOutMode == 1) c *= 2.0; // HDR10: allow highlights up to 2x paper white

    if (gHud > 0.5)
    {
        float2 hp = i.pos.xy - 16.0;
        if (hp.x >= 0 && hp.y >= 0 && hp.x < gHudSize.x && hp.y < gHudSize.y)
        {
            float4 h = gHudTex.Load(int3((int2)hp, 0));
            c = lerp(c, h.rgb, h.a);
        }
    }

    float3 enc;
    if (gOutMode == 0) enc = LinearToSRGB(c);
    else
    {
        float3 c2020 = mul(M709to2020, max(c, -0.2));
        enc = PQ(saturate(c2020 * gPaperWhite / 10000.0));
    }
    uint seed = pcg((uint)i.pos.x + pcg((uint)i.pos.y * 8191u + gFrame * 65521u));
    float n = rf(seed) + rf(seed) - 1.0;
    enc += n * gDither * (1.0 / 1023.0);
    return float4(enc, 1);
}
