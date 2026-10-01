using UnityEngine;

/// <summary>
/// UI sounds shared by the runtime panels: one always-active 3D audio source (a panel
/// may be deactivated while its close sound plays) and short generated tones, so no
/// audio asset is needed. Panels may replace the tones with their own clips (SetClips).
/// </summary>
public static class UISounds
{
    public static bool Enabled = true;
    /// <summary>0..1.</summary>
    public static float Volume = 0.5f;

    public static AudioClip Click => Ensure(ref click, "UI Click", 1400f, 1400f, 0.04f, 0.9f);
    public static AudioClip Hover => Ensure(ref hover, "UI Hover", 2200f, 2200f, 0.015f, 0.35f);
    public static AudioClip Open => Ensure(ref open, "UI Open", 600f, 1200f, 0.09f, 0.8f);
    public static AudioClip Close => Ensure(ref close, "UI Close", 1200f, 600f, 0.09f, 0.8f);

    private static AudioClip click;
    private static AudioClip hover;
    private static AudioClip open;
    private static AudioClip close;
    private static AudioSource source;

    /// <summary>Replaces the generated tones with assets (null keeps the tone).</summary>
    public static void SetClips(AudioClip clickClip, AudioClip hoverClip, AudioClip openClip, AudioClip closeClip)
    {
        if (clickClip != null) click = clickClip;
        if (hoverClip != null) hover = hoverClip;
        if (openClip != null) open = openClip;
        if (closeClip != null) close = closeClip;
    }

    /// <summary>Plays a clip from a world position (the panel), at full volume within reach.</summary>
    public static void Play(AudioClip clip, Vector3 position)
    {
        if (!Enabled || clip == null) return;
        if (source == null)
        {
            var go = new GameObject("UI Sounds") { hideFlags = HideFlags.DontSave };
            source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.minDistance = 3f;
        }
        source.transform.position = position;
        source.PlayOneShot(clip, Volume);
    }

    private static AudioClip Ensure(ref AudioClip clip, string name, float startHz, float endHz, float seconds, float amplitude)
    {
        if (clip == null) clip = Tone(name, startHz, endHz, seconds, amplitude);
        return clip;
    }

    /// <summary>Short sine tone gliding from startHz to endHz with a fast decay (a soft UI blip).</summary>
    private static AudioClip Tone(string name, float startHz, float endHz, float seconds, float amplitude)
    {
        const int rate = 44100;
        int samples = Mathf.Max(1, Mathf.RoundToInt(seconds * rate));
        var data = new float[samples];
        double phase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float hz = Mathf.Lerp(startHz, endHz, t);
            phase += 2.0 * Mathf.PI * hz / rate;
            float attack = Mathf.Clamp01(i / (rate * 0.002f));   // 2 ms fade-in: no click at the start
            float decay = Mathf.Exp(-5f * t);
            data[i] = (float)System.Math.Sin(phase) * amplitude * attack * decay;
        }
        AudioClip clip = AudioClip.Create(name, samples, 1, rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.DontSave;
        return clip;
    }
}
