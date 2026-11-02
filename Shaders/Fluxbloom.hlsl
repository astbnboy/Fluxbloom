// Fluxbloom - GPU generative screensaver. All stages in one file (SM 5.1, compiled at startup).
// Canvas space: y in [-1,1] (up), x in [-gAspect, gAspect]. Colours are authored in OKLCH and carried as
// continuous floating point linear light until the final 10-bit (or HDR10) dithered encode.
//
// Every "origin" is an independent form. Its type (d.w) selects what it is:
//   0  flow-only form (feedback flame/tendril/cellular flow)
//   1  Lissajous curve            2  spirograph / hypotrochoid-epitrochoid    3  Lorenz attractor (integrated)
//   4  superformula (2D)          5  superformula (3D shell)                  6  Klein bottle (4D rotation, projected)
//  10  Mandelbulb (raymarched)   11  quaternion Julia (4D slice, raymarched) 12  gyroid (raymarched)
//  13  Julia set (2D)            14  Voronoi tessellation (dynamic subdivision)
// Types 1-6 are drawn by locking a fraction of that origin's particles to the curve/surface; types 10-14 are drawn by the
// geometry pass (PS_Geo) at reduced resolution into a separate layer that is composited on top and fed to the bloom.

#define MAX_ORIGINS 24
#define PI 3.14159265359
#define TAU 6.28318530718

struct Origin
{
    float4 a; // pos.xy, radius, weight (0..1 lifecycle fade)
    float4 b; // rotation, symmetry (N-fold, 1 = none), stretch, kind (0..4 continuous flow style)
    float4 c; // hue (rad), chroma, lightness, hueSpread
    float4 d; // radial flow (signed: in/out), twist, density, TYPE
    float4 e; // hueGradient.xy (rad per canvas unit), freq, seed phase
    float4 f; // haze tint (linear rgb, premultiplied), curve weight (relative share of the dedicated curve-particle pool)
    float4 g; // type parameters (animated on the CPU, continuous)
    float4 h; // type parameters
};

cbuffer Frame : register(b0)
{
    float2 gRes;  float2 gInvRes;
    float gAspect; float gTime; float gDt; uint gFrame;
    float gExposure; float gBloom; float gDecay; float gInject;
    uint gParticleCount; uint gOriginCount; uint gGamutMode; uint gOutMode;
    float gPaperWhite; float gHud; float gFlowSpeed; float gDither;
    float gHaze; float gDetail; float gParticleSize; float gBrightness;
    float gAdvect; float gNa; float gCurveStart; float gNb; // gNa: flow-pool particles, gCurveStart/gNb: curve-pool range
    float4 gSeed;
    float2 gFlowTexSize; float2 gHudSize;
    float4 gAudio; // bass, mid, treble, beat (0 when no music is playing)
    float4 gGeo;   // x: raymarch step budget (0.4..1), y: extra fractal iterations, z: geometry gain, w: curve-particle emission
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
    float2 vel; float rnd; uint rg;   // rg: baked red/green as two halves (premultiplied emission x1024)
    float cb; float3 pad;             // cb: baked blue (premultiplied emission x1024); pad: Lorenz state
};

Texture2D<float4> gTex0 : register(t0);
Texture2D<float4> gTex1 : register(t1);
StructuredBuffer<Particle> gPS : register(t2);
Texture2D<float4> gHudTex : register(t3);
Texture2D<float2> gFlowTex : register(t4);
Texture2D<float4> gGeoTex : register(t5);
Texture2D<float4> gCurveTex : register(t6);
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

// ================================================================== geometry: rotation and projection helpers
float3x3 RotM(float3 a)
{
    float sx, cx, sy, cy, sz, cz;
    sincos(a.x, sx, cx); sincos(a.y, sy, cy); sincos(a.z, sz, cz);
    float3x3 Rx = float3x3(1, 0, 0, 0, cx, -sx, 0, sx, cx);
    float3x3 Ry = float3x3(cy, 0, sy, 0, 1, 0, -sy, 0, cy);
    float3x3 Rz = float3x3(cz, -sz, 0, sz, cz, 0, 0, 0, 1);
    return mul(Rz, mul(Ry, Rx));
}

float3 ObjAngles(Origin o)
{
    float spd = 0.45 + 0.35 * abs(o.d.y);
    float ph = o.e.w;
    return float3(gTime * 0.11 * spd + ph, gTime * 0.083 * spd + ph * 1.7, gTime * 0.052 * spd + ph * 2.3);
}

float2 ProjectForm(Origin o, float3 v, out float depth)
{
    float persp = 1.0 / (1.0 - v.z * 0.30);
    depth = saturate(0.5 + 0.5 * v.z);
    float2 q = v.xy * persp;
    q.x *= max(o.b.z, 0.2);
    return o.a.xy + rot2(q * o.a.z, o.b.x);
}

// superformula radius
float SF(float phi, float m, float n1, float n2, float n3)
{
    float a = pow(abs(cos(m * phi * 0.25)), n2) + pow(abs(sin(m * phi * 0.25)), n3);
    return pow(max(a, 1e-4), -1.0 / max(n1, 0.2));
}
// continuous morph between neighbouring integer symmetries
float SFm(float phi, float m, float n1, float n2, float n3)
{
    float m0 = floor(m), f = frac(m);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(SF(phi, m0, n1, n2, n3), SF(phi, m0 + 1.0, n1, n2, n3), f);
}

float3 LorenzF(float3 s, float rho)
{
    return float3(10.0 * (s.y - s.x), s.x * (rho - s.z) - s.y, s.x * s.y - 2.6666667 * s.z);
}

bool IsCurveType(Origin o)
{
    int type = (int)(o.d.w + 0.5);
    return type >= 1 && type <= 6;
}

// Position (canvas units) of a particle locked to a curve/surface form. u = hseed, v from rnd. Stateful only for Lorenz (p.pad).
float2 CurveEval(Origin o, inout Particle p, bool fresh, out float depth)
{
    int type = (int)(o.d.w + 0.5);
    float u = p.hseed, v = frac(p.rnd * 91.7), sel = frac(p.rnd * 53.1);
    float t = gTime, ph = o.e.w;
    float3 ang = ObjAngles(o);
    float3 tilt = float3(0.55 * sin(t * 0.07 + ph), 0.55 * sin(t * 0.05 + ph * 1.3), ang.z);
    float3 P = 0;

    if (type == 1) // Lissajous
    {
        float s = (u * o.h.x + t * 0.04) * TAU;
        P = float3(sin(o.g.x * s + o.g.w), sin(o.g.y * s), sin(o.g.z * s + o.g.w * 0.5));
        P = mul(RotM(ang), P);
    }
    else if (type == 2) // spirograph: continuous hypotrochoid <-> epitrochoid morph
    {
        float k = o.g.x, d = o.g.y, m = o.g.z;
        float q = k - m;
        float th = (u * o.g.w + t * 0.02) * TAU;
        float2 xy = float2(q * cos(th) + m * d * cos(q * th), q * sin(th) - d * sin(q * th)) / (abs(q) + d + 0.001);
        P = float3(xy, 0.25 * sin(th * 0.33));
        P = mul(RotM(tilt), P);
    }
    else if (type == 3) // Lorenz attractor, integrated per particle
    {
        float3 s = p.pad;
        if (fresh) s = float3((u * 2.0 - 1.0) * 18.0, (v * 2.0 - 1.0) * 24.0, 8.0 + frac(p.rnd * 37.1) * 34.0);
        float hstep = gDt * o.g.y / 3.0;
        [unroll] for (int i = 0; i < 3; i++)
        {
            float3 k1 = LorenzF(s, o.g.x);
            float3 k2 = LorenzF(s + hstep * k1, o.g.x);
            s += 0.5 * hstep * (k1 + k2);
        }
        p.pad = s;
        float3 w = float3(s.x / 22.0, (s.z - 25.0) / 22.0, s.y / 24.0);
        P = mul(RotM(float3(0.22, ang.y * 1.3, 0.0)), w);
    }
    else if (type == 4) // superformula 2D, nested contours
    {
        float phi = u * 2.0 * TAU;
        float r = SFm(phi, o.g.x, o.g.y, o.g.z, o.g.w);
        float lvl = (floor(v * 5.0) + 1.0) / 5.0;
        r = min(r, 2.2) / 1.5 * lvl;
        P = float3(r * cos(phi), r * sin(phi), 0.25 * lvl * sin(t * 0.2 + lvl * 3.0));
        P = mul(RotM(tilt), P);
    }
    else if (type == 5) // superformula 3D shell, latitude / longitude lines
    {
        float u1 = u, v1 = v;
        if (sel < 0.5) v1 = (floor(v * 14.0) + 0.5) / 14.0; else u1 = (floor(u * 22.0) + 0.5) / 22.0;
        float phi = u1 * 2.0 * TAU, th = (v1 - 0.5) * PI;
        float r1 = SFm(phi, o.g.x, o.g.y, o.g.z, o.g.w);
        float r2 = SFm(th, o.h.x, o.h.y, o.h.z, o.h.w);
        r1 = min(r1, 2.4); r2 = min(r2, 2.4);
        float3 w = float3(r1 * cos(phi) * r2 * cos(th), r1 * sin(phi) * r2 * cos(th), r2 * sin(th)) / 1.7;
        P = mul(RotM(ang), w);
    }
    else // type 6: Klein bottle (Lawson embedding in R4), rotated in 4D, projected to 3D
    {
        float u1 = u, v1 = v;
        if (sel < 0.5) v1 = (floor(v * 12.0) + 0.5) / 12.0; else u1 = (floor(u * 28.0) + 0.5) / 28.0;
        float uu = u1 * TAU, vv = v1 * TAU;
        float R = o.g.x, r = o.g.y;
        float4 X = float4((R + r * cos(vv)) * cos(uu), (R + r * cos(vv)) * sin(uu), r * sin(vv) * cos(uu * 0.5), r * sin(vv) * sin(uu * 0.5));
        float a1 = t * 0.17 * o.g.z + ph, a2 = t * 0.11 * o.g.z + ph * 1.9;
        float c1, s1, c2, s2; sincos(a1, s1, c1); sincos(a2, s2, c2);
        float x2 = c1 * X.x - s1 * X.w, w2 = s1 * X.x + c1 * X.w;
        float z2 = c2 * X.z - s2 * w2, w3 = s2 * X.z + c2 * w2;
        float sc = 2.2 / (3.4 - w3);
        float3 v3 = float3(x2, X.y, z2) * sc / 2.3;
        P = mul(RotM(ang), v3);
    }
    return ProjectForm(o, P, depth);
}

// ------------------------------------------------------------------ compute: particles
float PoolWeight(Origin o, bool poolB)
{
    if (poolB) return IsCurveType(o) ? o.a.w * o.f.w : 0.0;   // curve pool: only curve forms
    return o.a.w * o.d.z;                                      // flow pool: every form emits some haze/flow particles
}

void Respawn(inout Particle p, inout uint s, bool first, bool poolB)
{
    float W = 0;
    [loop] for (uint i = 0; i < gOriginCount; i++) W += PoolWeight(gO[i], poolB);
    if (W <= 1e-4) { p.life = 0; p.age = 0; p.pos = float2(99, 99); p.prev = p.pos; return; }
    float r = rf(s) * W, acc = 0; uint home = 0;
    [loop] for (uint j = 0; j < gOriginCount; j++) { acc += PoolWeight(gO[j], poolB); home = j; if (r <= acc) break; }
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
    p.hseed = rf(s); p.origin = home; p.rnd = rf(s); p.pad = 0;
}

// Colour + emission are evaluated once per particle here (continuous OKLCH, per-form hue/chroma/lightness with spatial hue gradient).
void BakeColor(inout Particle p, bool locked)
{
    Origin og = gO[min(p.origin, MAX_ORIGINS - 1)];
    float fade = smoothstep(0.0, 1.0, p.age) * smoothstep(0.0, 1.5, p.life - p.age);
    float h = og.c.x + (p.hseed - 0.5) * og.c.w + dot(og.e.xy, p.pos) + 0.25 * sin(p.age * 0.6 + p.hseed * TAU);
    float C = og.c.y * (0.55 + 0.9 * frac(p.hseed * 13.37 + p.rnd * 3.1));
    float L = og.c.z + 0.10 * (p.rnd - 0.5);
    bool big = p.rnd > 0.985;
    float rad = gParticleSize * (big ? 3.2 : 1.0);
    // curve-locked particles are drawn without feedback, so they carry the steady-state exposure (decay time) instead of one frame
    float gain = locked ? gGeo.w * gDecay * fade * (big ? 0.9 : 1.0) / max(rad, 0.5)          // curve pool: share of particles already scales with the form weight
                        : gInject * gDt * fade * og.a.w * (big ? 0.9 : 1.0) / max(rad, 0.5);
    gain *= 1.0 + gAudio.x * 0.6;
    if (locked)
    {
        gain *= (0.55 + 0.9 * p.vel.x);                                         // depth cue: nearer parts glow brighter
        if ((int)(og.d.w + 0.5) == 3) gain *= 0.30;                              // Lorenz trajectories pile up densely in the lobes
        gain *= 0.62 + 0.38 * sin(p.hseed * TAU * 3.0 - gTime * 1.6 + og.e.w);  // light pulses travelling along the curve
    }
    float3 c = LCH(L, C, h) * (gain * 1024.0);
    p.rg = f32tof16(c.r) | (f32tof16(c.g) << 16);
    p.cb = c.b;
}

[numthreads(64, 1, 1)]
void CS_Update(uint3 id : SV_DispatchThreadID)
{
    uint Na = (uint)gNa;
    if (id.x >= gParticleCount) return;
    bool poolB = id.x >= Na;                                   // threads [0,Na) = flow pool, [Na, Na+Nb) = curve pool
    uint i = poolB ? (uint)gCurveStart + (id.x - Na) : id.x;
    Particle p = gP[i];
    uint s = pcg(i * 9781u + gFrame * 6271u + asuint(gSeed.x));
    bool first = (p.life <= 0.0);
    float lim = 1.08;
    Origin oPrev = gO[min(p.origin, MAX_ORIGINS - 1)];
    bool oob = !poolB && (abs(p.pos.x) > gAspect * lim || abs(p.pos.y) > lim);
    bool wrongHome = poolB && !IsCurveType(oPrev);             // its form was replaced by a non-curve form: re-home
    // curve-pool particles carry the frame they were last updated in (vel.y); one that was parked while the pool was smaller is re-spawned
    // instead of streaking from its stale position
    float sinceUpd = (float)(gFrame & 1048575u) - p.vel.y; if (sinceUpd < 0.0) sinceUpd += 1048576.0;
    bool stale = poolB && sinceUpd > 1.5;
    bool dead = first || p.age >= p.life || oob || wrongHome || stale || p.origin >= gOriginCount;
    if (dead) Respawn(p, s, first, poolB); else p.age += gDt;
    if (p.life <= 0.0) { gP[i] = p; return; }

    Origin o = gO[min(p.origin, MAX_ORIGINS - 1)];
    if (poolB)
    {
        float depth;
        float2 np = CurveEval(o, p, dead, depth);
        p.prev = dead ? np : p.pos;
        p.pos = np;
        p.vel = float2(depth, (float)(gFrame & 1048575u));
    }
    else if (!dead)
    {
        float dt = gDt;
        float2 uv = CanvasToUv(p.pos);
        float2 v = gFlowTex.SampleLevel(sLin, uv, 0) * gFlowSpeed;
        v += OriginFlow(o, p.pos, gTime, 1.0) * 0.06 * gFlowSpeed * gDetail;
        p.vel = lerp(p.vel, v, 1.0 - exp(-dt * 3.0));
        p.prev = p.pos;
        p.pos += p.vel * dt;
    }
    BakeColor(p, poolB);
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
        if (o.a.w < 0.002) continue;
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
    Particle p = gPS[iid + (gPass.w > 0.5 ? (uint)gCurveStart : 0u)];
    bool alive = (p.life > 0.0) && (p.age < p.life) && (p.origin < gOriginCount);
    float rad = gParticleSize * (p.rnd > 0.985 ? 3.2 : 1.0);
    float3 col = float3(f16tof32(p.rg & 0xFFFF), f16tof32(p.rg >> 16), p.cb) * (1.0 / 1024.0);
    o.col = float4(col, 1);
    o.rad = rad;

    float2 a = CanvasToPx(p.prev), b = CanvasToPx(p.pos);
    float2 d = b - a; float len = length(d);
    float2 dir = len > 1e-4 ? d / len : float2(1, 0);
    float2 nrm = float2(-dir.y, dir.x);
    float ext = rad * 1.2 + 0.6;
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

// ================================================================== geometry pass (raymarched solids + 2D fractal / tessellation)
// Signed distance estimators. All return a conservative distance (scaled by the caller).
float DE_Bulb(float3 pos, float power, out float trap)
{
    float3 z = pos; float dr = 1.0; float r = 0.0; trap = 1e9;
    int it = 6 + (int)gGeo.y;
    [loop] for (int i = 0; i < it; i++)
    {
        r = length(z);
        if (r > 2.0) break;
        float theta = acos(clamp(z.z / r, -1.0, 1.0));
        float phi = atan2(z.y, z.x);
        dr = pow(r, power - 1.0) * power * dr + 1.0;
        float zr = pow(r, power);
        theta *= power; phi *= power;
        z = zr * float3(sin(theta) * cos(phi), sin(phi) * sin(theta), cos(theta)) + pos;
        trap = min(trap, dot(z, z));
    }
    return 0.5 * log(max(r, 1e-4)) * r / dr;
}

float DE_QJulia(float3 p, float4 c, float wslice, out float trap)
{
    float4 z = float4(p, wslice);
    float md2 = 1.0, mz2 = dot(z, z);
    trap = 1e9;
    int it = 7 + (int)gGeo.y;
    [loop] for (int i = 0; i < it; i++)
    {
        md2 *= 4.0 * mz2;
        z = float4(z.x * z.x - dot(z.yzw, z.yzw), 2.0 * z.x * z.yzw) + c;
        mz2 = dot(z, z);
        trap = min(trap, mz2);
        if (mz2 > 256.0) break;
    }
    return 0.25 * sqrt(mz2 / md2) * log(mz2);
}

// gyroid <-> Schwarz-P morph, thickened into a shell, clipped to a sphere
float DE_Gyroid(float3 p, Origin o, out float trap)
{
    float sc = o.g.x, th = o.g.y, bl = saturate(o.g.z);
    float3 q = p * sc;
    float3 s = sin(q), c = cos(q);
    float fg = s.x * c.y + s.y * c.z + s.z * c.x;
    float fp = (c.x + c.y + c.z) * 0.6;
    float f = lerp(fg, fp, bl);
    float3 gg = float3(c.x * c.y - s.z * s.x, -s.x * s.y + c.y * c.z, -s.y * s.z + c.z * c.x);
    float3 gp = -s * 0.6;
    float3 gr = lerp(gg, gp, bl);
    float d = (abs(f) - th) / (sc * max(length(gr), 0.3));
    trap = saturate(f * 0.5 + 0.5);
    return max(d * 0.8, length(p) - 1.05);
}

float MapObj(int type, float3 p, Origin o, out float trap)
{
    if (type == 10) return DE_Bulb(p, o.g.x, trap);
    if (type == 11) return DE_QJulia(p, o.g, o.h.x, trap);
    return DE_Gyroid(p, o, trap);
}

float4 Shade3D(int type, Origin o, float2 n)
{
    float3x3 M = RotM(ObjAngles(o));
    float3 rdv = normalize(float3(n * 0.5, 1.0));
    float3 ro = mul(float3(0, 0, -3.2), M);
    float3 rd = mul(rdv, M);
    float Rb = type == 10 ? 1.28 : (type == 11 ? 1.45 : 1.08);
    float b = dot(ro, rd), cc = dot(ro, ro) - Rb * Rb, hh = b * b - cc;
    if (hh < 0.0) return 0;
    float sq = sqrt(hh);
    float t = -b - sq, tmax = -b + sq;

    int maxSteps = (int)(80.0 * gGeo.x) + 16;
    float tr = 0, minD = 1e9; bool hit = false; int steps = 0;
    [loop] for (int i = 0; i < maxSteps; i++)
    {
        steps = i;
        float d = MapObj(type, ro + rd * t, o, tr);
        minD = min(minD, d);
        if (d < 0.0006 * t) { hit = true; break; }
        t += d * 0.85;
        if (t > tmax) break;
    }
    float L = o.c.z, C = o.c.y, h0 = o.c.x;
    if (!hit)
    {
        float closest = sqrt(max(dot(ro, ro) - b * b, 0.0));
        float g = exp(-minD * 9.0) * (1.0 - smoothstep(0.45 * Rb, Rb, closest));
        return float4(LCH(L * 0.8, C * 0.8, h0 + 0.3) * g * 0.22, g * 0.05);
    }
    float3 pos = ro + rd * t;
    float eps = 0.0009 * t + 0.0003, dm;
    float2 k = float2(1, -1);
    float3 nor = normalize(k.xyy * MapObj(type, pos + k.xyy * eps, o, dm) + k.yyx * MapObj(type, pos + k.yyx * eps, o, dm) +
                           k.yxy * MapObj(type, pos + k.yxy * eps, o, dm) + k.xxx * MapObj(type, pos + k.xxx * eps, o, dm));
    float trapN = type == 10 ? saturate(sqrt(tr) * 0.5) : (type == 11 ? saturate(tr * 0.3) : tr);
    nor = mul(M, nor); // back to view space for lighting
    float3 l1 = normalize(float3(0.6, 0.7, -0.5)), l2 = normalize(float3(-0.7, -0.2, -0.6));
    float dif = saturate(dot(nor, l1)) * 0.85 + saturate(dot(nor, l2)) * 0.30;
    float fres = pow(1.0 - saturate(dot(nor, -rdv)), 3.0);
    float ao = pow(saturate(1.0 - (float)steps / (float)maxSteps), 0.8);
    float hue = h0 + (trapN - 0.5) * o.c.w * 2.2 + nor.y * 0.35;
    float3 baseC = LCH(L * 0.92, C * 1.15, hue);
    float3 rimC = LCH(min(L + 0.18, 0.95), C * 0.6, hue + 0.7);
    float3 col = baseC * (0.16 + 1.05 * dif) * ao + rimC * fres * 0.55 * ao;
    return float4(col, 1.0);
}

float4 ShadeJulia2D(Origin o, float2 n)
{
    float m = 1.0 - smoothstep(0.6, 1.0, length(n));
    if (m <= 0.0) return 0;
    float2 z = n * o.g.z, c = o.g.xy;
    int maxI = (int)(70.0 * gGeo.x) + 24;
    float trap = 1e9, mz2 = 0; int i = 0;
    [loop] for (i = 0; i < maxI; i++)
    {
        z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
        mz2 = dot(z, z);
        trap = min(trap, abs(sqrt(mz2) - 0.8));
        if (mz2 > 256.0) break;
    }
    bool inside = (i >= maxI);
    float mu = (float)i - log2(max(0.5 * log2(max(mz2, 2.0)), 1e-3));
    float e = saturate(mu / (float)maxI);
    float hue = o.c.x + mu * 0.05 + trap * 0.9 * o.c.w;
    float bright = inside ? 0.30 * exp(-trap * 3.0) + 0.05 : pow(e, 2.2) * 1.8 + 0.5 * exp(-trap * 5.0) * e;
    float3 col = LCH(o.c.z * (inside ? 0.8 : 1.0), o.c.y * 1.15, hue) * bright;
    return float4(col * m, m * (inside ? 0.40 : 0.22));
}

void Vor(float2 p, float spd, out float edge, out float id)
{
    int2 ip = (int2)floor(p);
    float2 fp = frac(p);
    float d1 = 8, d2 = 8; int2 cid = int2(0, 0);
    [unroll] for (int j = -1; j <= 1; j++)
    [unroll] for (int i = -1; i <= 1; i++)
    {
        int2 cell = ip + int2(i, j);
        float2 jit = hashLat2(cell, 7u);
        jit = 0.5 + 0.5 * sin(gTime * spd + TAU * jit);
        float2 r = float2(i, j) + jit - fp;
        float d = dot(r, r);
        if (d < d1) { d2 = d1; d1 = d; cid = cell; }
        else if (d < d2) d2 = d;
    }
    edge = exp(-(sqrt(d2) - sqrt(d1)) * 9.0);
    id = hashLat(cid, 13u);
}

float4 ShadeVoronoi(Origin o, float2 n)
{
    float m = 1.0 - smoothstep(0.55, 1.0, length(n));
    if (m <= 0.0) return 0;
    float L = max(o.g.y, 0.0);
    float l0 = floor(L), f = L - l0; f = f * f * (3.0 - 2.0 * f);
    float2 pp = n * o.g.x;
    float e0, id0, e1, id1;
    Vor(pp * exp2(l0), o.g.w, e0, id0);
    Vor(pp * exp2(l0 + 1.0), o.g.w, e1, id1);
    float edge = max(e0, e1 * f);
    float id = lerp(id0, id1, f);
    float hue = o.c.x + id * o.c.w * 1.6;
    float pulse = 0.5 + 0.5 * sin(gTime * 0.8 + id * TAU);
    float3 fill = LCH(o.c.z * 0.75, o.c.y, hue) * (0.07 + 0.22 * pulse);
    float3 wall = LCH(min(o.c.z + 0.15, 0.95), o.c.y * 0.8, hue + 0.5) * 0.9;
    float3 col = lerp(fill, wall, edge);
    return float4(col * m, m * 0.45);
}

float4 PS_Geo(VSF i) : SV_Target
{
    float2 p = UvToCanvas(i.uv);
    float3 acc = 0; float alpha = 0;
    [loop] for (uint k = 0; k < gOriginCount; k++)
    {
        Origin o = gO[k];
        int type = (int)(o.d.w + 0.5);
        if (type < 10 || o.a.w < 0.01) continue;
        float2 q = rot2(p - o.a.xy, -o.b.x);
        float R = o.a.z * 1.15;
        float2 n = q / R;
        if (dot(n, n) > 1.7) continue;
        float4 s;
        if (type <= 12) s = Shade3D(type, o, n);
        else if (type == 13) s = ShadeJulia2D(o, n);
        else s = ShadeVoronoi(o, n);
        acc += s.rgb * o.a.w;
        alpha = max(alpha, s.a * o.a.w);
    }
    return float4(acc * gGeo.z, alpha);
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
    if (gPass.w > 0.5) c += (gGeoTex.SampleLevel(sLin, i.uv, 0).rgb * 0.8 + gCurveTex.SampleLevel(sLin, i.uv, 0).rgb * 1.2); // geometry and curves feed the glow
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
    float4 g = gGeoTex.SampleLevel(sLin, i.uv, 0);
    c = c * (1.0 - saturate(g.a) * 0.7) + g.rgb;   // geometry occludes the haze behind it, then adds its own light
    c += gCurveTex.SampleLevel(sLin, i.uv, 0).rgb;  // crisp curves (Lissajous, spirograph, Lorenz, superformula, Klein)
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
