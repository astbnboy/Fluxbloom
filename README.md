# Fluxbloom

A GPU-accelerated generative screensaver for super-ultrawide displays, built for a **Samsung Odyssey G9 (LC49G95T, 32:9, 5120 x 1440, up to 240 Hz)** driven by an **NVIDIA RTX 3060 Ti (8 GB)** on Windows 11.

Evolving flame-like filaments, tendrils, vortices and cellular membranes drift across the whole canvas. Several independent forms grow from anywhere (edges and corners included), overlap, merge and split. All pixels are produced on the GPU every frame; nothing is pre-rendered.

Original work. It does not copy Electric Sheep's code, artwork, or branding.

## Quick start

```powershell
.\build.ps1                       # builds dist\Fluxbloom.scr (needs the .NET 8 SDK; the portable one in .\tools is picked up automatically)
.\dist\Fluxbloom.exe /w           # try it in a window with the diagnostics overlay (Esc quits)
.\install.ps1 -TimeoutMinutes 10  # make it your screensaver (per-user, no admin)
.\dist\Fluxbloom.exe /c           # settings dialog
.\uninstall.ps1                   # remove it
```

Requirements on the PC that runs it: Windows 10/11 x64, a Direct3D 12 GPU, and the **.NET 8 Desktop Runtime (x64)**. For a PC without it, build with `.\build.ps1 -SelfContained` (about 150 MB, no runtime needed).

## Command line (the standard screensaver contract, plus dev modes)

| Argument | Behaviour |
|---|---|
| `/s` | Run the screensaver fullscreen. Ends on any key press, mouse button, mouse movement (> 6 px), focus loss, display change, or session switch. |
| `/c` (or no arguments) | Settings dialog. |
| `/p <hwnd>` | Live preview inside the Windows Screen Saver Settings thumbnail. Low quality, 30 fps. Exits when its parent window closes. |
| `/w` | Debug: resizable window on the main display, diagnostics overlay on, Esc quits, mouse does not. |
| `/bench <frames> <out.png> [quality 0-2] [density]` | Offscreen render at 5120 x 1440 with no window. Writes GPU timings to `%APPDATA%\Fluxbloom\bench.txt` and a PNG of the last frame. |

## Settings

Stored in `%APPDATA%\Fluxbloom\settings.json`; edit with `/c`.

| Setting | Meaning |
|---|---|
| Style | Mixed, Flame/filaments, Tendrils/branching, Cellular/membranes, Spirals/vortices. Biases which flow types new forms use. |
| Density | Number of simultaneous forms and particle count (20-200 %). |
| Motion speed | Scales flow velocity and drift (10-250 %). |
| Brightness | Output exposure. |
| Colour richness | Scales chroma (saturation) of every form. |
| Colour drift | Speed at which the whole palette rotates through hue over time (0 = frozen palette). |
| Quality / power | **Low power**: 0.7 M particles, render scale up to 0.6, 60 fps cap. **Balanced**: 1.6 M, scale up to 0.85. **Maximum**: 3.2 M, native resolution. |
| Frame-rate cap | Match display refresh (max 240), or 30 / 60 / 90 / 120 / 144 / 165 / 180 / 240. |
| V-Sync | On: presents on the display's refresh (interval = refresh / cap). Off: spin-paced to the cap, tearing allowed. |
| Adaptive quality | If GPU time exceeds the frame budget it lowers particle count first, then render scale; it raises them again when there is headroom. Changes are rate-limited (at most every 2.5 s). |
| HDR10 / wide gamut | Uses PQ / Rec.2020 output only if Windows HDR is enabled on that monitor; otherwise SDR sRGB. |
| Other monitors | Black by default. Optionally rendered at low quality, 60 fps, independent composition. |
| Diagnostics overlay | Fps, cap and refresh, CPU ms, GPU ms split by stage, render and output resolution, particle count, VRAM used/budget, output mode. Hidden unless enabled here or running `/w`. |
| Seed | 0 = a new composition every run; any other number reproduces the same one. |

Files written (all under `%APPDATA%\Fluxbloom`): `settings.json`, `run.log` (start-up facts and a stats line every 10 s), `bench.txt`, `error.log` (only if something fails).

## Architecture

C# / .NET 8 host with **Direct3D 12** and HLSL, using the Vortice.Windows bindings.

**Why D3D12 + C#:** D3D12 is the native Windows API for the RTX 30 series, has real compute, timestamp queries, and flip-model swapchains with tearing and HDR colour spaces, and needs nothing beyond the OS. C# keeps the Windows plumbing (per-monitor windows, settings UI, screensaver contract) small and maintainable. The machine had no C++ toolchain or Windows SDK, so a portable .NET 8 SDK was installed into `.\tools` (git-ignored, removable). The CPU only orchestrates: it advances the composition (a few dozen floats), uploads one constant buffer, and records about 30 GPU commands per frame.

**Per-frame GPU graph** (`Shaders/Fluxbloom.hlsl`, one file, compiled at start-up with FXC to SM 5.1):

1. `CS_Flow` (compute): evaluates the flow field of every active form on a coarse grid (1/8 render resolution).
2. `CS_Update` (compute): advances millions of particles through that field plus a per-form high-frequency detail term, respawns dead ones, and bakes each particle's colour and emission once.
3. `PS_Field` (fullscreen): feedback pass. The previous frame is advected along the flow field and decayed (this makes flame-like persistence and soft volumetric glow), and a faint haze from each form is added.
4. `VS/PS_Particle`: every particle is drawn as an anti-aliased oriented capsule with additive blending into a floating-point HDR target.
5. Bloom: 4-level down/up chain.
6. `PS_Composite`: tone-map, encode (sRGB or HDR10 PQ), 10-bit dither, optional HUD.

**Composition (`Director.cs`)** keeps up to 24 independent forms. Each has its own position, size, anisotropy (stretch and rotation, so structures can sweep across the full width), N-fold symmetry (1-12), flow style (spiral, sinusoidal flame, Julia-like, curl-noise tendrils, cellular), inward/outward and twist, density, and colour. A coverage grid decays over about 22 s and steers new forms toward the least-covered part of the canvas, with deliberate spawns at edges and corners, so there are no persistent dead zones. Forms fade in and out over 8-22 s, wander, merge (one drifts into another and dissolves), and split (a child spawns from a parent). A slow "mood" oscillation moves the scene between sparse (about 3 forms) and dense (about 17-22). Ambient particles spawn uniformly everywhere.

**Colour model.** Colours are authored in **OKLCH** (perceptual lightness / chroma / hue) as continuous floats. There are no palettes or indexed colours: each form has its own hue, chroma, lightness and hue-spread, plus a spatial hue gradient, and each particle adds its own hue/chroma jitter, so hues vary continuously across forms and regions while the whole wheel rotates slowly. Accumulation is in 16-bit-float linear light. Out-of-gamut colours are desaturated toward grey (hue-preserving) into sRGB, or into Rec.2020 on the HDR path. The output is a 10-bit swapchain (1,024 levels per channel, about 1.07 billion colours) with triangular dither to suppress banding; with Windows HDR on it switches to HDR10 (PQ, Rec.2020).

**Panel safety:** everything moves, brightness breathes slowly, and the overlay is off by default, so no static bright element persists. There are no abrupt full-screen changes; forms and palette change over many seconds.

**Power and resources:** presentation is vsync-paced; rendering stops for minimised windows; no clocks, power limits, voltages, fans or driver settings are touched. VRAM use is 160 / 290 / 480 MB at Low / Balanced / Maximum at 5120 x 1440; the renderer prefers the GPU with the most dedicated memory, steps down a quality tier at start-up if the VRAM budget is too small (or if resource creation fails), and rebuilds its targets at a lower scale at run time instead of failing. (Test hook: set `FLX_VRAM_BUDGET_MB=600` to simulate a small budget.)

## Measured on this machine

Offscreen at 5120 x 1440 (no window), RTX 3060 Ti, GPU timestamps averaged over the last 120 frames of a 500-900 frame run:

| Quality | Render size | Particles | GPU ms/frame | GPU-bound fps | VRAM |
|---|---|---|---|---|---|
| Maximum | 5120 x 1440 (1.00) | 3.2 M | 8.5 - 9.1 | about 110 - 118 | 476 MB |
| Balanced | 4352 x 1224 (0.85) | 1.6 M | 4.3 | about 233 | 288 MB |
| Low power | 3072 x 864 (0.60) | 0.7 M | 2.0 | about 505 | 156 MB |

Adaptive-quality stress test (Maximum, density 200, cap 240, V-Sync off): started at 121 fps, shed particles then stepped render scale 1.00 > 0.90 > 0.80 > 0.70 with live resource rebuilds, and held 240.1 fps. Because the GPU idles between frames, its clocks drop at low frame rates, so GPU ms at 60 Hz looks higher than at 240 Hz; this is normal.

## Limitations (please read)

- **The panel is currently not in its native mode.** On this PC Windows reports the G9 as 3840 x 1080 at 59 Hz. The renderer follows whatever mode Windows reports (it was verified fullscreen at 3840 x 1080), and 5120 x 1440 was verified only as an offscreen render. To use the panel fully, set 5120 x 1440 and 240 Hz under Settings > System > Display > Advanced display (the G9 needs DisplayPort 1.4 with DSC for 240 Hz).
- **HDR10 / Rec.2020 output is implemented but untested** (no HDR-enabled display was available). SDR output is the tested path.
- Windows' own Screen Saver Settings list shows only screensavers in `System32`. The default per-user install works without admin and runs normally, but may show as blank in that drop-down; use `.\install.ps1 -System` (elevated) to have it listed by name.
- The Settings > Preview thumbnail was tested with a stand-in parent window, not Windows' own dialog.
- Shaders are compiled at start-up with FXC (Shader Model 5.1, `d3dcompiler_47.dll`, part of Windows) rather than DXC / SM 6, because nothing here needs SM 6 features.
- The "On resume, display logon screen" lock option is a Windows setting this project deliberately does not change.

## Repo layout

```
Fluxbloom.csproj        project (net8.0-windows, WinForms for the settings UI only)
Program.cs              entry point and argument handling
SaverHost.cs            per-monitor windows, input-exit, preview mode, benchmark
Renderer.cs             Direct3D 12 renderer, resource management, timing, adaptive quality, HUD
Director.cs             composition: forms, lifecycle, merge/split, coverage-driven placement
ColorMath.cs            OKLCH to linear sRGB (CPU mirror of the shader)
Settings.cs, SettingsForm.cs, RunLog.cs
Shaders/Fluxbloom.hlsl  all GPU stages
build.ps1  install.ps1  uninstall.ps1
```

## Troubleshooting

- Nothing happens / immediate exit: check `%APPDATA%\Fluxbloom\error.log` and `run.log`.
- Lower than expected frame rate: run `/w` and read the overlay (GPU ms per stage); lower Quality or leave Adaptive quality on.
- Double-clicking a `.scr` runs it as `/s` (Windows file association). To pass other arguments use `Fluxbloom.exe`, which is the identical binary.
