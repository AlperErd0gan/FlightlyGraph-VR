using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A small orb low in the view that shows the voice assistant (GeminiLiveAssistant):
/// it breathes slowly in blue while you hold push-to-talk (listening), pulses in amber with
/// the loudness of the answer while Gemini speaks, and fades away otherwise. It floats
/// a little below and to the left of where you look and follows your head smoothly.
/// Self-contained: it adds itself when the scene has a GeminiLiveAssistant and changes no
/// other script.
/// </summary>
public class GeminiOrb : MonoBehaviour
{
    public GeminiLiveAssistant gemini;
    [Tooltip("Place in front of the eyes (m): x right, y up, z forward.")]
    public Vector3 offset = new Vector3(-0.16f, -0.2f, 0.6f);
    [Tooltip("Orb diameter (m).")]
    public float size = 0.035f;
    [Tooltip("How quickly the orb follows the head (higher = snappier).")]
    public float follow = 6f;
    public Color listeningColor = new Color(0.35f, 0.75f, 1f, 1f);
    public Color speakingColor = new Color(1f, 0.7f, 0.3f, 1f);

    private Transform orb;
    private Transform halo;
    private Material coreMaterial;
    private Material haloMaterial;
    private AudioSource voice;
    private InputAction talk;
    private float shown;      // 0 = hidden, 1 = fully shown
    private float level;      // smoothed loudness of the answer, 0..1
    private readonly float[] samples = new float[256];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<GeminiOrb>() != null) return;
        GeminiLiveAssistant assistant = FindFirstObjectByType<GeminiLiveAssistant>();
        if (assistant != null) assistant.gameObject.AddComponent<GeminiOrb>();
    }

    private void Awake()
    {
        if (gemini == null) gemini = FindFirstObjectByType<GeminiLiveAssistant>();
        if (gemini == null)
        {
            enabled = false;
            return;
        }
        // Same buttons as the assistant's push-to-talk.
        talk = new InputAction("Gemini Orb Listening", InputActionType.Button);
        if (!string.IsNullOrEmpty(gemini.pushToTalkBinding)) talk.AddBinding(gemini.pushToTalkBinding);
        if (!string.IsNullOrEmpty(gemini.pushToTalkKey)) talk.AddBinding(gemini.pushToTalkKey);
        Build();
    }

    private void OnEnable() => talk?.Enable();

    private void OnDisable() => talk?.Disable();

    private void OnDestroy()
    {
        talk?.Dispose();
        if (orb != null) Destroy(orb.gameObject);
        if (coreMaterial != null) Destroy(coreMaterial);
        if (haloMaterial != null) Destroy(haloMaterial);
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null || orb == null) return;
        if (voice == null) voice = gemini.GetComponentInChildren<AudioSource>();

        bool ready = gemini.isActiveAndEnabled && gemini.IsReady;
        bool speaking = ready && gemini.IsSpeaking;
        bool listening = ready && !speaking && talk.IsPressed();
        shown = Mathf.MoveTowards(shown, speaking || listening ? 1f : 0f, Time.deltaTime * 4f);
        orb.gameObject.SetActive(shown > 0.01f);
        if (!orb.gameObject.activeSelf) return;

        // Loudness of the answer (RMS of what the voice is playing), smoothed.
        float loud = 0f;
        if (speaking && voice != null)
        {
            voice.GetOutputData(samples, 0);
            float sum = 0f;
            foreach (float s in samples) sum += s * s;
            loud = Mathf.Clamp01(Mathf.Sqrt(sum / samples.Length) * 6f);
        }
        level = Mathf.Lerp(level, loud, 1f - Mathf.Exp(-15f * Time.deltaTime));

        // Breathing while listening, beating with the voice while speaking.
        float pulse = speaking ? 1f + 0.45f * level : 1f + 0.12f * Mathf.Sin(Time.time * 4f);
        orb.localScale = Vector3.one * size * pulse * Mathf.Lerp(0.6f, 1f, shown);
        Color c = speaking ? speakingColor : listeningColor;
        c.a = shown;
        coreMaterial.color = c;
        Color h = c;
        h.a = shown * (speaking ? 0.15f + 0.35f * level : 0.18f);
        haloMaterial.color = h;

        // Follow the head: a spot below / left of the view, turned with the head around the vertical.
        Transform head = cam.transform;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = head.forward;
        Quaternion facing = Quaternion.LookRotation(forward.normalized, Vector3.up);
        Vector3 target = head.position + facing * offset;
        bool snap = (orb.position - target).sqrMagnitude > 4f; // first frame or a teleport
        orb.position = snap ? target : Vector3.Lerp(orb.position, target, 1f - Mathf.Exp(-follow * Time.deltaTime));
    }

    private void Build()
    {
        orb = MakeSphere("Gemini Orb", out coreMaterial).transform;
        halo = MakeSphere("Gemini Orb Halo", out haloMaterial).transform;
        halo.SetParent(orb, false);
        halo.localScale = Vector3.one * 1.8f;
        orb.gameObject.SetActive(false);
    }

    private static GameObject MakeSphere(string name, out Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>()); // must never block XR rays
        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        material = new Material(Shader.Find("Sprites/Default")) { name = name + " (runtime)" };
        r.sharedMaterial = material;
        return go;
    }
}
