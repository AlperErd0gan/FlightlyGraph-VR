using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Live flights: the control panel of FlightSimulator, a slim panel below eye level like
/// the timeline. Play / pause, ±15 minutes, the simulated UTC time and date with the
/// aircraft in the air, playback speed and close; below, the aircraft in the air over
/// the whole window with a time slider on top. While it is open the aircraft are drawn,
/// the routes are dimmed behind them and the graph shows the simulated day's month
/// (sizes and routes of that month); closing restores both.
/// Opened from the dashboard (Live flights), by voice, or P in the Editor ([ / ] speed).
/// </summary>
public class FlightPanel : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Found in the scene if unset; added to this object if the scene has none.")]
    public FlightSimulator simulator;
    [Tooltip("Closed when the flight panel opens (both sit below eye level). Found in the scene if unset.")]
    public TimelinePanel timeline;

    [Tooltip("Route brightness while the panel is open (0 = hidden, 1 = normal), so the aircraft stand out.")]
    [Range(0f, 1f)] public float routeDimming = 0.35f;
    [Tooltip("Seconds jumped by the previous / next buttons.")]
    public float stepSeconds = 900f;

    [Header("Placement")]
    public float distance = 0.95f;
    public float heightOffset = -0.46f;
    public float followAngle = 50f;
    public float snapDistance = 2f;
    public float followSpeed = 3f;

    private static readonly Vector2 Size = new Vector2(960f, 236f);
    private const float Pad = 24f;
    private const int ChartBins = 96;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Canvas canvas;
    private TextMeshProUGUI timeText;
    private TextMeshProUGUI statsText;
    private UIIcon playIcon;
    private Button speedButton;
    private Slider slider;
    private UIChart chart;
    private float[] profile;
    private bool following;
    private int periodBefore = -1;
    private float nextTextUpdate;
    private InputAction playKey;
    private InputAction fasterKey;
    private InputAction slowerKey;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;
    public bool Available => simulator != null && simulator.IsReady && simulator.FlightCount > 0;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (simulator == null) simulator = FindFirstObjectByType<FlightSimulator>();
        if (simulator == null) simulator = gameObject.AddComponent<FlightSimulator>();
        if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
        playKey = new InputAction("Live Flights Play", InputActionType.Button, "<Keyboard>/p");
        fasterKey = new InputAction("Live Flights Faster", InputActionType.Button, "<Keyboard>/rightBracket");
        slowerKey = new InputAction("Live Flights Slower", InputActionType.Button, "<Keyboard>/leftBracket");
    }

    private void OnEnable()
    {
        playKey.Enable();
        fasterKey.Enable();
        slowerKey.Enable();
    }

    private void OnDisable()
    {
        playKey.Disable();
        fasterKey.Disable();
        slowerKey.Disable();
    }

    private void OnDestroy()
    {
        playKey.Dispose();
        fasterKey.Dispose();
        slowerKey.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    private System.Collections.IEnumerator Start()
    {
        UIKit.EnsureXREventSystem();
        while (simulator == null || !simulator.IsReady)
        {
            if (simulator == null) yield break;
            yield return null;
        }
        Build();
        canvas.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (canvas == null) return;
        bool locked = GuidedTour.InputLocked;
        if (locked && IsOpen) Close(); // the tour has its own captions and shows all months
        if (IsOpen)
        {
            foreach (BaseRaycaster raycaster in canvas.GetComponents<BaseRaycaster>()) raycaster.enabled = !locked;
        }
        if (locked) return;
        if (playKey.WasPressedThisFrame())
        {
            if (!IsOpen) Open();
            else SetPlaying(!simulator.IsPlaying);
        }
        if (IsOpen && fasterKey.WasPressedThisFrame()) ChangeSpeed(+1);
        if (IsOpen && slowerKey.WasPressedThisFrame()) ChangeSpeed(-1);
        // The timeline was opened over us: it owns the month now.
        if (IsOpen && timeline != null && timeline.IsOpen) Close();
        if (IsOpen && Time.unscaledTime >= nextTextUpdate) Refresh();
    }

    private void LateUpdate()
    {
        if (!IsOpen) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Vector3 target = TargetPosition(head);
        Transform panel = canvas.transform;
        if (Vector3.Distance(panel.position, target) > snapDistance)
        {
            Place(head);
            following = false;
            return;
        }
        Vector3 toPanel = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
        float angle = toPanel.sqrMagnitude > 1e-6f ? Vector3.Angle(UIKit.FlatForward(head), toPanel) : 0f;
        if (!following && angle > followAngle) following = true;
        if (following)
        {
            panel.position = Vector3.Lerp(panel.position, target, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            if (Vector3.Distance(panel.position, target) < 0.03f) following = false;
        }
        Vector3 away = panel.position - head.position;
        if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.LookRotation(away, Vector3.up);
    }

    private Vector3 TargetPosition(Transform head) => head.position + UIKit.FlatForward(head) * distance + Vector3.up * heightOffset;

    private void Place(Transform head)
    {
        canvas.transform.position = TargetPosition(head);
        canvas.transform.rotation = Quaternion.LookRotation(canvas.transform.position - head.position, Vector3.up);
    }

    // ---- Public controls -------------------------------------------------

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>Shows the aircraft and the panel, puts the graph on the simulated day's month and plays.</summary>
    public void Open()
    {
        if (canvas == null || !Available) return;
        if (timeline != null && timeline.IsOpen) timeline.Close();
        Camera cam = Camera.main;
        if (cam != null)
        {
            canvas.worldCamera = cam;
            Place(cam.transform);
        }
        following = false;
        canvas.gameObject.SetActive(true);
        UISounds.Play(UISounds.Open, canvas.transform.position);

        periodBefore = graph.HasPeriod ? graph.Period : -1;
        int month = SimMonth();
        if (month >= 0) graph.SetPeriod(month);
        graph.SetEdgeDimming(routeDimming);
        simulator.Visible = true;
        SetPlaying(true);
        Refresh();
    }

    /// <summary>Hides the aircraft and the panel; the graph goes back to the month (or all months) it showed before.</summary>
    public void Close()
    {
        if (canvas == null || !IsOpen) return;
        SetPlaying(false);
        simulator.Visible = false;
        graph.SetEdgeDimming(1f);
        if (graph.HasTimeAxis) graph.SetPeriod(periodBefore);
        UISounds.Play(UISounds.Close, canvas.transform.position);
        canvas.gameObject.SetActive(false);
    }

    public void SetPlaying(bool value)
    {
        if (value) simulator.Play();
        else simulator.Pause();
        if (playIcon != null) playIcon.SetShape(value ? UIIcon.Shape.Pause : UIIcon.Shape.Play);
        nextTextUpdate = 0f;
    }

    /// <summary>Jumps by `seconds` (clamped to the window) and pauses.</summary>
    public void Jump(float seconds)
    {
        SetPlaying(false);
        simulator.Seek(simulator.SimTime + seconds);
        Refresh();
    }

    public void ChangeSpeed(int direction)
    {
        simulator.StepSpeed(direction);
        Refresh();
    }

    // Index of the simulated day's month in the graph's time axis, or -1.
    private int SimMonth()
    {
        if (!graph.HasTimeAxis) return -1;
        string period = simulator.SimUtc.ToString("yyyy-MM", Inv);
        return Array.IndexOf(graph.Meta.periods, period);
    }

    // ---- UI --------------------------------------------------------------

    private void Build()
    {
        canvas = UIKit.WorldCanvas("Live Flights", Size);
        Transform root = canvas.transform;
        Image background = UIKit.Box(root, "Background", UIKit.PanelColor, UIKit.PanelRadius);
        UIKit.Stretch(background.rectTransform);

        Button play = UIKit.Button(root, "", () => SetPlaying(!simulator.IsPlaying), Pad, Pad, 64f, 64f,
                                   UIKit.ButtonStyle.Primary, 30f, UIIcon.Shape.Play);
        playIcon = play.GetComponentInChildren<UIIcon>();
        UIKit.Button(root, "", () => Jump(-stepSeconds), Pad + 76f, Pad + 8f, 48f, 48f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Previous);
        UIKit.Button(root, "", () => Jump(stepSeconds), Pad + 128f, Pad + 8f, 48f, 48f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Next);

        timeText = UIKit.Text(root, "", UIKit.TitleSize, UIKit.TextColor, Pad + 196f, Pad - 2f, 420f, 50f, TextAlignmentOptions.Left);
        timeText.fontStyle = FontStyles.Bold;
        timeText.textWrappingMode = TextWrappingModes.NoWrap;
        statsText = UIKit.Text(root, "", UIKit.SmallSize, UIKit.MutedTextColor, Pad + 198f, Pad + 44f, 520f, 26f, TextAlignmentOptions.Left);

        float right = Size.x - Pad;
        UIKit.Button(root, "", Close, right - 52f, Pad + 6f, 52f, 52f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Close);
        speedButton = UIKit.Button(root, "1 min/s", () => ChangeSpeed(+1), right - 52f - 12f - 130f, Pad + 6f, 130f, 52f,
                                   UIKit.ButtonStyle.Secondary, UIKit.SmallSize);

        float chartX = Pad + 10f;
        float chartY = 108f;
        float chartW = Size.x - 2f * (Pad + 10f);
        float chartH = 74f;
        UIKit.Card(root, Pad, chartY - 8f, Size.x - 2f * Pad, chartH + 16f, UIKit.SurfaceColor).name = "Chart Background";
        RectTransform chartRect = UIKit.NewRect("Chart", root);
        UIKit.Place(chartRect, chartX, chartY, chartW, chartH);
        chart = chartRect.gameObject.AddComponent<UIChart>();
        chart.color = UIKit.AccentColor;
        chart.highlightColor = UIKit.HighlightColor;
        chart.axisColor = new Color(1f, 1f, 1f, 0f);
        chart.lineWidth = 3f;
        chart.raycastTarget = false;
        profile = simulator.AirborneProfile(ChartBins);
        chart.SetData(profile, UIChart.Kind.Line, -1);
        slider = BuildSlider(root, chartX, chartY - 8f, chartW, chartH + 16f);

        // Hour ticks under the chart (every 3 h for a day, every hour for a few hours).
        int hours = Mathf.Max(1, Mathf.RoundToInt(simulator.DurationSec / 3600f));
        int every = hours > 8 ? 3 : 1;
        DateTime start = simulator.SimUtc.AddSeconds(-simulator.SimTime);
        for (int h = 0; h <= hours; h += every)
        {
            float x = chartX + chartW * Mathf.Clamp01(h * 3600f / Mathf.Max(1, simulator.DurationSec));
            UIKit.Text(root, start.AddHours(h).ToString("HH:mm", Inv), UIKit.SmallSize, UIKit.MutedTextColor, x - 40f,
                       chartY + chartH + 12f, 80f, 24f, TextAlignmentOptions.Top);
        }
        Refresh();
    }

    private Slider BuildSlider(Transform root, float x, float y, float w, float h)
    {
        RectTransform rect = UIKit.NewRect("Slider", root);
        UIKit.Place(rect, x, y, w, h);
        Image hitArea = rect.gameObject.AddComponent<Image>();
        hitArea.color = new Color(1f, 1f, 1f, 0f); // invisible, but catches rays and clicks
        Slider s = rect.gameObject.AddComponent<Slider>();
        s.direction = Slider.Direction.LeftToRight;
        s.minValue = 0f;
        s.maxValue = 1f;
        s.transition = Selectable.Transition.None;
        Navigation nav = s.navigation;
        nav.mode = Navigation.Mode.None;
        s.navigation = nav;

        RectTransform area = UIKit.NewRect("Handle Area", rect);
        UIKit.Stretch(area);
        RectTransform handle = UIKit.NewRect("Handle", area);
        handle.anchorMin = new Vector2(0f, 0f);
        handle.anchorMax = new Vector2(0f, 1f);
        handle.sizeDelta = new Vector2(22f, 0f);
        Image bar = UIKit.Box(handle, "Bar", new Color(1f, 1f, 1f, 0.55f), 0f);
        bar.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        bar.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        bar.rectTransform.sizeDelta = new Vector2(3f, 0f);
        Image knob = UIKit.Box(handle, "Knob", UIKit.HighlightColor, 11f);
        knob.rectTransform.anchorMin = knob.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        knob.rectTransform.sizeDelta = new Vector2(22f, 22f);
        s.handleRect = handle;
        s.targetGraphic = knob;

        s.onValueChanged.AddListener(value =>
        {
            SetPlaying(false);
            simulator.Seek(value * simulator.DurationSec);
            Refresh();
        });
        return s;
    }

    // Texts and slider from the simulator (four times a second while playing).
    private void Refresh()
    {
        if (canvas == null) return;
        nextTextUpdate = Time.unscaledTime + 0.25f;
        DateTime now = simulator.SimUtc;
        timeText.text = now.ToString("HH:mm", Inv) + " UTC";
        statsText.text = now.ToString("ddd d MMM yyyy", Inv) + "  ·  " + simulator.ActiveCount.ToString("N0", Inv) + " aircraft in the air" +
                         (simulator.IsPlaying ? "" : "  ·  paused");
        float duration = Mathf.Max(1, simulator.DurationSec);
        slider.SetValueWithoutNotify(simulator.SimTime / duration);
        chart.SetData(profile, UIChart.Kind.Line,
                      Mathf.Clamp(Mathf.FloorToInt(simulator.SimTime / duration * ChartBins), 0, ChartBins - 1));
        if (playIcon != null) playIcon.SetShape(simulator.IsPlaying ? UIIcon.Shape.Pause : UIIcon.Shape.Play);
        UIKit.SetButtonLabel(speedButton, SpeedLabel(simulator.Speed));
    }

    /// <summary>60 -> "1 min/s", 30 -> "30 s/s", 3600 -> "1 h/s".</summary>
    public static string SpeedLabel(float secondsPerSecond)
    {
        if (secondsPerSecond >= 3600f) return (secondsPerSecond / 3600f).ToString("0.#", Inv) + " h/s";
        if (secondsPerSecond >= 60f) return (secondsPerSecond / 60f).ToString("0.#", Inv) + " min/s";
        return secondsPerSecond.ToString("0", Inv) + " s/s";
    }
}
