# Fluxbloom

A GPU-accelerated generative screensaver for super-ultrawide displays, written for a **Samsung Odyssey G9 (32:9, 5120 x 1440, up to 240 Hz)** driven by an **NVIDIA RTX 3060 Ti** on Windows 11. Direct3D 12, HLSL compute and pixel shaders, C# / .NET 8.

Ten kinds of mathematical form appear on a sparse, flowing fractal-flame background. **Only one to three of them are on screen at any time**, chosen at random and cross-fading in and out over many seconds, each growing from anywhere on the canvas (edges and corners included) and transforming continuously. This keeps the screen calm and the GPU load low. Everything is computed on the GPU every frame; there is no pre-rendered content. Original work; it does not copy Electric Sheep's code, artwork or branding.

## The geometry library

| Form | What you see | How it is drawn |
|---|---|---|
| **Mandelbulb** | 3D fractal solid with extremely fine surface detail; its power morphs between about 5.5 and 10.5 | Raymarched distance estimator, orbit-trap colouring |
| **Quaternion Julia set** | 4D fractal shown as a 3D slice; the quaternion constant and slice position drift continuously | Raymarched 4D distance estimator |
| **Julia set** | Classic complex-plane fractal; the parameter c travels around the Julia "lotus" circle | Per-pixel escape time with smooth colouring |
| **Gyroid** | Triply periodic minimal surface shell that morphs into the Schwarz-P surface and breathes in scale and thickness | Raymarched implicit surface |
| **Voronoi tessellation** | Irregular cells with moving feature points that continuously subdivide into finer levels | Two blended Voronoi levels per pixel |
| **Lorenz attractor** | Butterfly trajectories of a genuinely chaotic system; rho drifts through the chaotic range | Each particle integrates the Lorenz ODE (Heun) every frame |
| **Lissajous curve** | Flowing, looping figures whose frequency ratios drift, tumbling in 3D | Curve-locked particles, light pulses travel along the curve |
| **Spirograph / hypotrochoid** | Interlocking flower-like rosettes; continuous morph between hypotrochoid and epitrochoid | Parametric curve particles |
| **Superformula** | Flowers, stars and shells. 2D: nested contours. 3D: a latitude/longitude wireframe shell. Symmetry m morphs continuously between integers | Parametric curve / surface particles |
| **Klein bottle** | Non-orientable surface (Lawson embedding in R4), rotated through 4D and projected, so it passes through itself | Parametric surface wireframe, 4D rotation + 4D to 3D perspective |
| **Flow forms** | Flame-like filaments, tendrils, vortices, cellular membranes, with N-fold symmetry | Feedback field + particles (the original engine) |

The scheduler never shows more than three geometry forms and never leaves the screen without one: it re-rolls how many to show (1 to the maximum) every 30-80 s, picks types that are not already on screen, retires forms one at a time, and staggers their lifetimes so replacements always overlap. `Fluxbloom.exe /dirtest <hours>` simulates the scheduler headlessly and verifies this (40 simulated hours, 5 seeds: always 1 to 3, average 2.3). All parameters are animated by slow sine mixtures, so every transformation is continuous; nothing pops. Forms carry their own colour (see below), size, anisotropy and orientation, so a Lissajous figure can sweep the full 5120 px width while a Mandelbulb sits in a corner.

## Quick start

```powershell
.\build.ps1                       # builds dist\Fluxbloom.scr (needs the .NET 8 SDK)
.\dist\Fluxbloom.exe /w           # try it in a window with the diagnostics overlay (Esc quits)
.\install.ps1 -TimeoutMinutes 10  # make it your screensaver (per-user, no admin)
.\dist\Fluxbloom.exe /c           # settings dialog
.\uninstall.ps1                   # remove it
```

Needs on the PC that runs it: Windows 10/11 x64, a Direct3D 12 GPU, and the **.NET 8 Desktop Runtime (x64)**. For a PC without it, build with `.\build.ps1 -SelfContained` (about 150 MB, no runtime needed).

## Command line

| Argument | Behaviour |
|---|---|
| `/s` | Screensaver, fullscreen. Ends on key press, mouse button, mouse movement (> 6 px), focus loss, display change or session switch. |
| `/c` (or none) | Settings dialog. |
| `/p <hwnd>` | Live preview in the Windows Screen Saver Settings thumbnail (low quality, 30 fps). |
| `/w` | Debug window on the main display with the diagnostics overlay (Esc quits). |
| `/bench <frames> <out.png> [quality 0-2] [density]` | Offscreen render at 5120 x 1440 with no window; GPU timings go to `%APPDATA%\Fluxbloom\bench.txt`. |
| `/dirtest [hours]` | Headless scheduler check (see above); results in `%APPDATA%\Fluxbloom\dirtest.txt`. |

Test environment variables: `FLX_TYPES=10,11` forces only those form types (codes in the table above: 1 Lissajous, 2 spirograph, 3 Lorenz, 4 superformula 2D, 5 superformula 3D, 6 Klein, 10 Mandelbulb, 11 quaternion Julia, 12 gyroid, 13 Julia set, 14 Voronoi); `FLX_FAKE_AUDIO=1` drives the audio hooks with a synthetic 120 BPM signal; `FLX_VRAM_BUDGET_MB=600` simulates a small VRAM budget.

## Settings

Stored in `%APPDATA%\Fluxbloom\settings.json`; edit with `/c`.

| Setting | Meaning |
|---|---|
| Style | Mixed (flow + geometry), Flame, Tendrils, Cellular, Spirals, **Mathematical geometry only**, **Fractals only (Mandelbulb, quaternion Julia, Julia)**. |
| Geometry forms on screen (max) | 0-3 (default 3). The scene shows between 1 and this many at a time; 0 turns geometry off. With a maximum below 3, one extra form can be present for a few seconds while one cross-fades into the next. |
| Density | Number of flow-background forms (20-200 %). |
| Motion speed | Scales flow velocity and drift. |
| Brightness, Colour richness, Colour drift | Exposure; chroma scale; speed of the palette's hue rotation. |
| Quality / power | **Low power**: render scale up to 0.6, fewer particles, 60 fps cap. **Balanced**: scale up to 0.85. **Maximum**: native resolution, most detail. |
| Frame-rate cap, V-Sync | Match display refresh (max 240) or 30-240 fps; present on refresh or spin-pace. |
| Adaptive quality | Lowers particle count first, then render scale, only when the GPU cannot hold the frame budget; raises them again when there is headroom. |
| HDR10 / wide gamut | PQ / Rec.2020 output only if Windows HDR is on for that monitor (implemented, untested). |
| Other monitors | Black by default, or rendered at low quality. |
| Diagnostics overlay | Fps, CPU and GPU ms per stage (including the geometry pass), resolution, particle count, VRAM, output mode. Off by default. |
| Seed | 0 = new composition each run. |

Files under `%APPDATA%\Fluxbloom`: `settings.json`, `run.log`, `bench.txt`, `error.log` (only on failure).

## Architecture

**Host:** C# / .NET 8 with Vortice.Windows bindings for Direct3D 12 and the FXC shader compiler (SM 5.1, compiled at start-up). The CPU only orchestrates: it advances the composition (a few dozen floats), uploads one constant buffer, and records about 40 GPU commands per frame.

**Per-frame GPU graph** (`Shaders/Fluxbloom.hlsl`):

1. `CS_Flow`: flow field of all forms on a coarse grid.
2. `CS_Update`: advances two particle pools. The *flow pool* follows the field; the *curve pool* is locked to the Lissajous / spirograph / Lorenz / superformula / Klein forms (Lorenz particles integrate their own state). Colour and emission are baked per particle once.
3. `PS_Field`: feedback pass (advect + decay the previous frame, add haze), then flow particles as additive anti-aliased capsules into an HDR float target. This gives the flame-like persistence.
4. Curve layer: curve-pool particles are drawn into their own cleared layer with no feedback, so line work stays crisp.
5. `PS_Geo`: geometry layer at reduced resolution: raymarched Mandelbulb / quaternion Julia / gyroid, Julia set, Voronoi.
6. Bloom (4-level chain, fed by scene + geometry + curves), then composite: occlusion of haze by solid geometry, tone-map, sRGB or HDR10 PQ encode, 10-bit dither.

**Composition (`Director.cs`)** keeps up to 24 forms in stable GPU slots. A coverage grid steers new forms toward the least-covered part of the canvas (with deliberate spawns at edges and corners), so there are no persistent dead zones. A slow mood oscillation moves between sparse and dense scenes; forms merge and split. The particle pools are sized from the live scene, so quiet scenes cost less.

**Colour model.** Colours are authored in **OKLCH** as continuous floats; there are no palettes or indexed colours. Each form has its own hue, chroma, lightness and hue spread, plus a spatial hue gradient, and fractals add orbit-trap hue variation. The whole wheel rotates slowly. Accumulation is 16-bit-float linear light; out-of-gamut colours desaturate toward grey (hue-preserving). Output is 10-bit (about 1.07 billion colours) with triangular dithering, or HDR10 / Rec.2020 when Windows HDR is on.

**Panel safety.** Everything moves, brightness breathes, and the overlay is off by default, so no static bright element persists.

**Power and resources.** Presentation is vsync-paced; rendering stops for minimised windows; no clocks, power limits, voltages, fans or driver settings are touched. The renderer steps down a quality tier at start-up if the VRAM budget is too small or resource creation fails, and rebuilds its targets at a lower scale at run time rather than failing.

## Planned: music

The animation is built so a looping music track can drive it without the visuals ever looping. `AudioFeatures.cs` exposes four smoothed values (bass, mid, treble, beat pulse, each 0..1). They reach the GPU in the per-frame constants and already modulate form radius (bass), Mandelbulb power (mid), gyroid thickness (treble), particle emission (bass) and exposure (beat); they are all zero today, so nothing changes. A future audio engine (file or loopback capture, FFT, beat detector) only has to write those four numbers each frame. Scene time is a separate, monotonic clock, so the music can restart at its loop point while the animation simply carries on. `FLX_FAKE_AUDIO=1` shows the hooks responding to a synthetic 120 BPM beat.

## Measured performance

Offscreen at 5120 x 1440, RTX 3060 Ti, default Mixed style, four different compositions (1 to 3 geometry forms each), 700 frames, GPU timestamps:

| Quality | Render size | GPU ms (four scenes) | Average | GPU-bound fps | VRAM |
|---|---|---|---|---|---|
| Maximum | 5120 x 1440 (1.00) | 5.0 / 7.6 / 8.7 / 9.8 | 7.7 ms | about 129 | 586 MB |
| Balanced | 4352 x 1224 (0.85) | 2.6 / 3.7 / 4.5 / 4.9 | 3.9 ms | about 254 | 357 MB |
| Low power | 3072 x 864 (0.60) | 1.1 / 1.5 / 1.9 / 2.1 | 1.7 ms | about 597 | 188 MB |

Balanced fits a 240 Hz frame budget (4.2 ms). With Adaptive quality on, Maximum degrades gracefully when a scene is heavy.

## Limitations

- **Display mode.** On the development PC Windows reported the G9 as 3840 x 1080 at 59 Hz rather than 5120 x 1440 at 240 Hz. The renderer follows whatever mode Windows reports (fullscreen verified at 3840 x 1080); 5120 x 1440 was verified as an offscreen render. Set the native mode under Settings > System > Display > Advanced display (240 Hz needs DisplayPort 1.4 with DSC).
- HDR10 / Rec.2020 output is implemented but untested.
- The Klein bottle is drawn as a wireframe of the Lawson R4 embedding (so it can be rotated through the fourth dimension); the quaternion Julia set is a 3D slice of the 4D set.
- Windows' Screen Saver Settings drop-down lists only screensavers in `System32`. The per-user install works without admin but may show blank there; `.\install.ps1 -System` (elevated) lists it by name.
- Shaders are compiled with FXC at start-up (SM 5.1, `d3dcompiler_47.dll`, part of Windows); nothing here needs SM 6.
- The "On resume, display logon screen" lock option is a Windows setting that this project does not change.
- Released under the MIT licence (see `LICENSE`).

## Repo layout

```
Fluxbloom.csproj        project (net8.0-windows; WinForms only for the settings UI)
Program.cs  SaverHost.cs        entry point, per-monitor windows, input exit, preview, benchmark
Renderer.cs             Direct3D 12 renderer: pools, passes, timing, adaptive quality, HUD
Director.cs             composition: forms, lifecycle, merge/split, per-type parameter animation
AudioFeatures.cs        hook for the planned music feature
ColorMath.cs  Settings.cs  SettingsForm.cs  RunLog.cs
Shaders/Fluxbloom.hlsl  all GPU stages and the geometry library
build.ps1  install.ps1  uninstall.ps1
```

## Troubleshooting

- Nothing happens / immediate exit: see `%APPDATA%\Fluxbloom\error.log` and `run.log`.
- Lower frame rate than expected: run `/w` and read the overlay; lower Quality or leave Adaptive quality on.
- Double-clicking a `.scr` runs it as `/s` (Windows file association). Use `Fluxbloom.exe`, the identical binary, to pass other arguments.
