using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

/// <summary>
/// Debug tool (like MicTest): frame rate and frame times, toggled with F3. While on, a small
/// text near the top right of the view shows the last second (average FPS, worst frame, frames
/// over the headset's budget) and one CSV row per second goes to Application.persistentDataPath
/// (perf_&lt;date&gt;_&lt;time&gt;.csv, path logged), with what was going on: 3D or map view,
/// timeline playing, live flights playing, Gemini speaking. Over Link these are the PC's frame
/// times, not the Quest's own; standalone numbers need an APK build (or OVR Metrics Tool).
/// Self-contained: it adds itself when the scene has a GraphLoader and changes no other script.
/// </summary>
public class PerfMonitor : MonoBehaviour
{
    [Tooltip("Key that turns the monitor on / off (Input System path).")]
    public string toggleKey = "<Keyboard>/f3";
    public bool showOnStart = false;
    [Tooltip("Write one CSV row per second while the monitor is on.")]
    public bool writeCsv = true;
    [Tooltip("Frame rate budget when the headset does not report its refresh rate.")]
    public float targetFps = 72f;
    [Tooltip("Place of the text in front of the eyes (m): x right, y up, z forward.")]
    public Vector3 offset = new Vector3(0.2f, 0.16f, 0.7f);

    private const float Window = 1f;           // seconds per row / text update
    private const float FlushEvery = 5f;       // seconds between file writes
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private InputAction toggle;
    private bool on;
    private Canvas canvas;
    private TextMeshProUGUI text;

    // Current window.
    private float windowStart;
    private int frames;
    private float sum;
    private float worst;
    private int overBudget;
    private float budget = 1f / 72f;        // seconds per frame, read again each window

    private string csvPath;
    private readonly StringBuilder pending = new StringBuilder();
    private float lastFlush;
    private float startTime;

    // Context, found again until present (several are added at runtime).
    private GeoMapView map;
    private TimelinePanel timeline;
    private FlightSimulator flights;
    private GeminiLiveAssistant gemini;
    private float nextLookup;
    private readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<PerfMonitor>() != null) return;
        if (FindFirstObjectByType<GraphLoader>() == null) return;
        new GameObject("PerfMonitor").AddComponent<PerfMonitor>();
    }

    private void Awake()
    {
        toggle = new InputAction("Perf Monitor", InputActionType.Button, toggleKey);
        Build();
        SetOn(showOnStart);
    }

    private void OnEnable() => toggle?.Enable();

    private void OnDisable()
    {
        toggle?.Disable();
        Flush();
    }

    private void OnDestroy()
    {
        toggle?.Dispose();
        Flush();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    private void Update()
    {
        if (toggle.WasPressedThisFrame()) SetOn(!on);
        if (!on) return;

        float dt = Time.unscaledDeltaTime;
        frames++;
        sum += dt;
        if (dt > worst) worst = dt;
        if (dt > budget * 1.05f) overBudget++; // small tolerance for timer jitter

        float now = Time.unscaledTime;
        if (now - windowStart >= Window) EndWindow(now);
        if (writeCsv && now - lastFlush >= FlushEvery) Flush();
    }

    private void LateUpdate()
    {
        if (!on || canvas == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        canvas.transform.position = head.position + head.rotation * offset;
        canvas.transform.rotation = Quaternion.LookRotation(canvas.transform.position - head.position, head.up);
    }

    private void SetOn(bool value)
    {
        on = value;
        canvas.gameObject.SetActive(on);
        ResetWindow(Time.unscaledTime);
        if (on && writeCsv && csvPath == null) StartCsv();
        if (!on) Flush();
        Debug.Log("PerfMonitor: " + (on ? "on" : "off") + (on && csvPath != null ? $" (CSV: {csvPath})" : ""));
    }

    private void ResetWindow(float now)
    {
        windowStart = now;
        frames = 0;
        sum = 0f;
        worst = 0f;
        overBudget = 0;
        budget = 1f / RefreshRate();
    }

    private void EndWindow(float now)
    {
        if (frames > 0)
        {
            float avgMs = sum / frames * 1000f;
            float fps = frames / Mathf.Max(1e-4f, sum);
            float worstMs = worst * 1000f;
            float budgetFps = 1f / budget;
            FindContext(now);
            string view = map != null && map.IsMap ? "map" : "3d";
            bool timelinePlaying = timeline != null && timeline.IsPlaying;
            bool flightsPlaying = flights != null && flights.IsPlaying;
            bool speaking = gemini != null && gemini.IsSpeaking;

            text.text = string.Format(Inv, "{0:F0} FPS  ({1:F1} ms)\nworst {2:F1} ms · {3} / {4} over {5:F0} Hz\n{6}{7}{8}{9}",
                                      fps, avgMs, worstMs, overBudget, frames, budgetFps, view,
                                      timelinePlaying ? " · timeline" : "", flightsPlaying ? " · flights" : "",
                                      speaking ? " · Gemini" : "");
            text.color = overBudget > frames / 10 ? UIKit.BadColor : UIKit.TextColor;

            if (writeCsv && csvPath != null)
            {
                pending.Append((now - startTime).ToString("F1", Inv)).Append(',')
                       .Append(SceneManager.GetActiveScene().name).Append(',')
                       .Append(fps.ToString("F1", Inv)).Append(',')
                       .Append(avgMs.ToString("F2", Inv)).Append(',')
                       .Append(worstMs.ToString("F2", Inv)).Append(',')
                       .Append(frames.ToString(Inv)).Append(',')
                       .Append(overBudget.ToString(Inv)).Append(',')
                       .Append(budgetFps.ToString("F1", Inv)).Append(',')
                       .Append(view).Append(',')
                       .Append(timelinePlaying ? '1' : '0').Append(',')
                       .Append(flightsPlaying ? '1' : '0').Append(',')
                       .Append(speaking ? '1' : '0').Append('\n');
            }
        }
        ResetWindow(now);
    }

    /// <summary>The headset's refresh rate when XR runs, else targetFps.</summary>
    private float RefreshRate()
    {
        SubsystemManager.GetSubsystems(displays);
        foreach (XRDisplaySubsystem d in displays)
        {
            if (d.running && d.TryGetDisplayRefreshRate(out float hz) && hz > 1f) return hz;
        }
        return Mathf.Max(1f, targetFps);
    }

    private void FindContext(float now)
    {
        if (now < nextLookup) return;
        nextLookup = now + 5f;
        if (map == null) map = FindFirstObjectByType<GeoMapView>();
        if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
        if (flights == null) flights = FindFirstObjectByType<FlightSimulator>();
        if (gemini == null) gemini = FindFirstObjectByType<GeminiLiveAssistant>();
    }

    // ---- CSV -----------------------------------------------------------------

    private void StartCsv()
    {
        startTime = Time.unscaledTime;
        lastFlush = startTime;
        string name = "perf_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".csv";
        csvPath = Path.Combine(Application.persistentDataPath, name);
        try
        {
            File.WriteAllText(csvPath, "time_s,scene,fps_avg,frame_ms_avg,frame_ms_worst,frames,over_budget,budget_fps," +
                                       "view,timeline_playing,flights_playing,gemini_speaking\n");
        }
        catch (IOException e)
        {
            Debug.LogWarning($"PerfMonitor: cannot write {csvPath} ({e.Message}); no CSV.");
            csvPath = null;
        }
    }

    private void Flush()
    {
        lastFlush = Time.unscaledTime;
        if (csvPath == null || pending.Length == 0) return;
        try
        {
            File.AppendAllText(csvPath, pending.ToString());
        }
        catch (IOException e)
        {
            Debug.LogWarning($"PerfMonitor: cannot write {csvPath} ({e.Message}).");
        }
        pending.Clear();
    }

    // ---- Text ----------------------------------------------------------------

    private void Build()
    {
        canvas = UIKit.WorldCanvas("Perf Monitor", new Vector2(300f, 90f));
        // Display only: no rays should stop on it.
        foreach (var raycaster in canvas.GetComponents<UnityEngine.UI.GraphicRaycaster>()) raycaster.enabled = false;
        foreach (var raycaster in canvas.GetComponents<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>()) raycaster.enabled = false;
        UnityEngine.UI.Image background = UIKit.Box(canvas.transform, "Background", new Color(0f, 0f, 0f, 0.55f), 10f);
        UIKit.Stretch(background.rectTransform);
        text = UIKit.Text(canvas.transform, "", 16f, UIKit.TextColor, 10f, 8f, 280f, 74f);
        text.textWrappingMode = TextWrappingModes.NoWrap;
    }
}
