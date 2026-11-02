namespace Fluxbloom;

/// <summary>
/// Hook for the planned music feature. A future audio engine (looping track, FFT) only has to write these four values every
/// frame, each smoothed to 0..1: bass, mid, treble, and a beat pulse that decays after each detected beat.
///
/// The animation clock never depends on the audio: scene time keeps running forward forever, while the music is free to loop.
/// Audio only modulates amplitudes (form radius, fractal power, gyroid thickness, particle emission, exposure).
///
/// Until that engine exists everything here is 0 and has no effect. Set FLX_FAKE_AUDIO=1 to drive the hooks with a synthetic
/// 120 BPM signal, to see how the visuals will respond.
/// </summary>
public static class AudioFeatures
{
    public static volatile float Bass, Mid, Treble, Beat;
    static readonly bool Fake = Environment.GetEnvironmentVariable("FLX_FAKE_AUDIO") == "1";
    static float smBass;

    /// <summary>Called once per frame by the renderer with the scene clock (used only by the synthetic test signal).</summary>
    public static void Tick(float sceneTime, float dt)
    {
        if (!Fake) return;
        float ph = sceneTime * 2.0f % 1.0f;               // 120 BPM
        float kick = MathF.Exp(-7f * ph);
        smBass += (kick - smBass) * MathF.Min(1f, dt * 20f);
        Bass = smBass;
        Beat = kick;
        Mid = 0.5f + 0.5f * MathF.Sin(sceneTime * 1.7f);
        Treble = 0.5f + 0.5f * MathF.Sin(sceneTime * 5.3f) * (ph < 0.5f ? 1f : 0.3f);
    }
}
