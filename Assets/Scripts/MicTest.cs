using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Microphone check before voice commands: shows in front of the user which microphone is
/// used and a live input level, and records 3 seconds and plays them back (right
/// controller B, or M in the Editor) so you can hear what the app actually receives.
/// Asks for the microphone permission on Quest (Android). Independent of the Voice SDK.
/// Temporary: remove it from the scene once the microphone is confirmed.
/// </summary>
public class MicTest : MonoBehaviour
{
    [Tooltip("Sample rate for the test recording.")]
    public int sampleRate = 16000;
    public float recordSeconds = 3f;
    [Tooltip("Level (dB) shown as a full bar.")]
    public float loudDb = -10f;
    [Tooltip("Level (dB) shown as an empty bar.")]
    public float quietDb = -60f;
    public float distance = 1.0f;
    public float height = 0.25f;

    private const int LoopSeconds = 1;
    private const int Window = 512;

    private string device;
    private AudioClip loopClip;
    private AudioSource playback;
    private TextMeshPro display;
    private InputAction recordAction;
    private readonly float[] window = new float[Window];
    private float peakDb = -80f;
    private float loudestSinceStart = -80f;
    private bool recording;
    private float recordEnds;
    private string status = "";

    private void Awake()
    {
        recordAction = new InputAction("Mic Test Record", InputActionType.Button);
        recordAction.AddBinding("<XRController>{RightHand}/{SecondaryButton}"); // B
        recordAction.AddBinding("<Keyboard>/m");

        display = new GameObject("MicTest Display").AddComponent<TextMeshPro>();
        display.fontSize = 0.3f;
        display.alignment = TextAlignmentOptions.Center;
        display.textWrappingMode = TextWrappingModes.Normal;
        display.rectTransform.sizeDelta = new Vector2(1.4f, 0.6f);

        playback = gameObject.AddComponent<AudioSource>();
        playback.playOnAwake = false;
        playback.spatialBlend = 0f;
    }

    private void OnEnable() => recordAction.Enable();

    private void OnDisable() => recordAction.Disable();

    private void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
        }
#endif
    }

    private void OnDestroy()
    {
        recordAction.Dispose();
        if (device != null || loopClip != null) Microphone.End(device);
        if (display != null) Destroy(display.gameObject);
    }

    private void Update()
    {
        if (!recording) EnsureListening();
        if (recordAction.WasPressedThisFrame() && !recording) StartRecording();
        if (recording && Time.time >= recordEnds) StopRecordingAndPlay();

        float db = recording ? -80f : CurrentLevelDb();
        if (db > peakDb) peakDb = db;
        else peakDb = Mathf.MoveTowards(peakDb, db, 20f * Time.deltaTime); // slow fall, like a VU meter
        loudestSinceStart = Mathf.Max(loudestSinceStart, db);

        UpdateDisplay(db);
    }

    // ---- Microphone --------------------------------------------------------

    private void EnsureListening()
    {
        if (Microphone.devices.Length == 0)
        {
            status = "No microphone found";
            return;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            status = "Microphone permission not granted";
            return;
        }
#endif
        if (loopClip != null && Microphone.IsRecording(device)) return;
        device = PickDevice();
        loopClip = Microphone.Start(device, true, LoopSeconds, sampleRate);
        status = loopClip != null ? "Listening" : "Microphone.Start failed";
        Debug.Log($"MicTest: devices = [{string.Join(", ", Microphone.devices)}], using '{device}', status: {status}");
    }

    /// <summary>The headset's microphone when listed (over Link the PC's own microphone may come first).</summary>
    private static string PickDevice()
    {
        foreach (string name in Microphone.devices)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("oculus") || lower.Contains("quest") || lower.Contains("headset")) return name;
        }
        return Microphone.devices[0];
    }

    private float CurrentLevelDb()
    {
        if (loopClip == null || !Microphone.IsRecording(device)) return -80f;
        int position = Microphone.GetPosition(device) - Window;
        if (position < 0) position += loopClip.samples;
        if (position < 0 || position + Window > loopClip.samples) return -80f;
        loopClip.GetData(window, position);
        float sum = 0f;
        foreach (float s in window) sum += s * s;
        float rms = Mathf.Sqrt(sum / Window);
        return rms > 1e-5f ? 20f * Mathf.Log10(rms) : -80f;
    }

    private void StartRecording()
    {
        if (Microphone.devices.Length == 0) return;
        Microphone.End(device);
        loopClip = Microphone.Start(device, false, Mathf.CeilToInt(recordSeconds), sampleRate);
        recording = loopClip != null;
        recordEnds = Time.time + recordSeconds;
        status = recording ? "Recording... speak now" : "Recording failed";
    }

    private void StopRecordingAndPlay()
    {
        recording = false;
        Microphone.End(device);
        playback.clip = loopClip;
        playback.Play();
        status = "Playing back the recording";
        Debug.Log("MicTest: recorded " + recordSeconds + " s, playing back");
        loopClip = null; // listening restarts on the next frame, after the playback clip is kept
    }

    // ---- Display -------------------------------------------------------------

    private void UpdateDisplay(float db)
    {
        Camera cam = Camera.main;
        if (cam != null)
        {
            Transform head = cam.transform;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 target = head.position + forward * distance + Vector3.up * height;
            display.transform.position = Vector3.Lerp(display.transform.position, target, 1f - Mathf.Exp(-3f * Time.deltaTime));
            display.transform.rotation = Quaternion.LookRotation(display.transform.position - head.position, Vector3.up);
        }

        int bars = 20;
        int filled = Mathf.RoundToInt(Mathf.InverseLerp(quietDb, loudDb, peakDb) * bars);
        var bar = new StringBuilder();
        for (int i = 0; i < bars; i++) bar.Append(i < filled ? "|" : ".");
        string colour = peakDb > -30f ? "#7CFF7C" : peakDb > -50f ? "#FFD060" : "#FF7070";

        display.text =
            $"<b>Microphone test</b>\n" +
            $"<size=70%>{(device ?? "-")}</size>\n" +
            $"<color={colour}>{bar}</color>  {peakDb:0} dB\n" +
            $"<size=70%>{status} · loudest so far {loudestSinceStart:0} dB\n" +
            $"Press B (M in the Editor) to record 3 s and hear it back</size>";
    }
}
