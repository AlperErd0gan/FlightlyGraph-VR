using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Time slider for the graph: a slim panel below eye level with the month shown,
/// the network's flights per day over all months (the slider runs over that chart),
/// play / pause, playback speed and "All months". Moving the slider or playing calls
/// GraphLoader.SetPeriod, so airport sizes, the visible routes, the selection and the
/// info panel follow that month (the COVID drop and the recovery play out in the graph).
/// Opened from the data dashboard, with T in the Editor, or Open(); closing it returns
/// the graph to all months. Needs a dataset with a time axis (meta file).
/// </summary>
public class TimelinePanel : MonoBehaviour
{
    public GraphLoader graph;
    public bool startOpen = false;

    [Header("Playback")]
    [Tooltip("Months per second; the speed button cycles through these.")]
    public float[] speeds = { 1f, 2f, 4f };
    public int startSpeed = 1;
    [Tooltip("Month shown when the timeline opens while the graph shows all months: 0 = the first month.")]
    public int openAtPeriod = 0;

    [Header("Placement")]
    [Tooltip("Distance in front of the eyes (m).")]
    public float distance = 0.95f;
    [Tooltip("Height of the panel centre relative to the eyes (m); below eye level so it does not cover the graph, " +
             "and below the info panel.")]
    public float heightOffset = -0.46f;
    [Tooltip("Starts following once the panel is this many degrees (horizontally) away from where you look.")]
    public float followAngle = 50f;
    [Tooltip("Beyond this (e.g. after a teleport) the panel jumps instead of gliding (m).")]
    public float snapDistance = 2f;
    public float followSpeed = 3f;

    private static readonly Vector2 Size = new Vector2(960f, 236f);
    private const float Pad = 24f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] MonthNames =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    private Canvas canvas;
    private TextMeshProUGUI monthText;
    private TextMeshProUGUI statsText;
    private UIIcon playIcon;
    private Button allButton;
    private Button speedButton;
    private Slider slider;
    private UIChart chart;
    private float[] perDay;
    private int speedIndex;
    private bool playing;
    private float stepTimer;
    private bool following;
    private InputAction toggleKey;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;
    public bool IsPlaying => playing;
    /// <summary>Months per second (speeds may be edited at runtime, so the index is clamped).</summary>
    private float CurrentSpeed => speeds != null && speeds.Length > 0 ? speeds[Mathf.Clamp(speedIndex, 0, speeds.Length - 1)] : 1f;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        toggleKey = new InputAction("Toggle Timeline", InputActionType.Button, "<Keyboard>/t");
        speedIndex = Mathf.Clamp(startSpeed, 0, Mathf.Max(0, speeds.Length - 1));
    }

    private void OnEnable()
    {
        toggleKey.Enable();
        if (graph != null) graph.PeriodChanged += SyncFromGraph;
    }

    private void OnDisable()
    {
        toggleKey.Disable();
        if (graph != null) graph.PeriodChanged -= SyncFromGraph;
    }

    private void OnDestroy()
    {
        toggleKey.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    private System.Collections.IEnumerator Start()
    {
        UIKit.EnsureXREventSystem();
        while (graph == null || !graph.IsLoaded) yield return null;
        if (!graph.HasTimeAxis)
        {
            Debug.Log("TimelinePanel: the dataset has no time axis (meta file); timeline unavailable.");
            yield break;
        }
        Build();
        canvas.gameObject.SetActive(false);
        if (startOpen) Open();
    }

    private void Update()
    {
        if (canvas == null) return;
        bool locked = GuidedTour.InputLocked;
        if (IsOpen)
        {
            foreach (BaseRaycaster raycaster in canvas.GetComponents<BaseRaycaster>()) raycaster.enabled = !locked;
        }
        if (!locked && toggleKey.WasPressedThisFrame()) Toggle();
        if (locked && IsOpen) Close(); // the guided tour shows all months and its own captions here
        if (!playing || !IsOpen) return;

        stepTimer += Time.deltaTime * CurrentSpeed;
        while (stepTimer >= 1f && playing)
        {
            stepTimer -= 1f;
            int next = graph.Period + 1;
            if (next >= graph.PeriodCount)
            {
                SetPlaying(false); // stop on the last month; Play starts over
                break;
            }
            graph.SetPeriod(next);
        }
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
            float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            panel.position = Vector3.Lerp(panel.position, target, t);
            if (Vector3.Distance(panel.position, target) < 0.03f) following = false;
        }
        // Tilted to face the eyes: readable although it sits below eye level.
        Vector3 away = panel.position - head.position;
        if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.LookRotation(away, Vector3.up);
    }

    private Vector3 TargetPosition(Transform head)
    {
        return head.position + UIKit.FlatForward(head) * distance + Vector3.up * heightOffset;
    }

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

    /// <summary>Shows the timeline; if the graph shows all months, jumps to openAtPeriod.</summary>
    public void Open()
    {
        if (canvas == null) return; // no time axis
        Camera cam = Camera.main;
        if (cam != null)
        {
            canvas.worldCamera = cam;
            Place(cam.transform);
        }
        following = false;
        canvas.gameObject.SetActive(true);
        UISounds.Play(UISounds.Open, canvas.transform.position);
        if (!graph.HasPeriod) graph.SetPeriod(Mathf.Clamp(openAtPeriod, 0, graph.PeriodCount - 1));
        SyncFromGraph();
    }

    /// <summary>Hides the timeline and returns the graph to all months.</summary>
    public void Close()
    {
        if (canvas == null || !IsOpen) return;
        SetPlaying(false);
        UISounds.Play(UISounds.Close, canvas.transform.position);
        canvas.gameObject.SetActive(false);
        graph.SetPeriod(-1);
    }

    public void SetPlaying(bool value)
    {
        if (value && graph.PeriodCount > 0 && (!graph.HasPeriod || graph.Period >= graph.PeriodCount - 1))
        {
            graph.SetPeriod(0); // from all months or the last month: start over
        }
        playing = value;
        stepTimer = 0f;
        if (playIcon != null) playIcon.SetShape(playing ? UIIcon.Shape.Pause : UIIcon.Shape.Play);
    }

    public void Step(int delta)
    {
        SetPlaying(false);
        int from = graph.HasPeriod ? graph.Period : (delta > 0 ? -1 : graph.PeriodCount);
        graph.SetPeriod(Mathf.Clamp(from + delta, 0, graph.PeriodCount - 1));
    }

    // ---- UI --------------------------------------------------------------

    private void Build()
    {
        canvas = UIKit.WorldCanvas("Timeline", Size);
        Transform root = canvas.transform;
        Image background = UIKit.Box(root, "Background", UIKit.PanelColor, UIKit.PanelRadius);
        UIKit.Stretch(background.rectTransform);

        // Top row: play, previous / next, month and stats, all months, speed, close.
        Button play = UIKit.Button(root, "", () => SetPlaying(!playing), Pad, Pad, 64f, 64f,
                                   UIKit.ButtonStyle.Primary, 30f, UIIcon.Shape.Play);
        playIcon = play.GetComponentInChildren<UIIcon>();
        UIKit.Button(root, "", () => Step(-1), Pad + 76f, Pad + 8f, 48f, 48f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Previous);
        UIKit.Button(root, "", () => Step(+1), Pad + 128f, Pad + 8f, 48f, 48f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Next);

        monthText = UIKit.Text(root, "", UIKit.TitleSize, UIKit.TextColor, Pad + 196f, Pad - 2f, 360f, 50f, TextAlignmentOptions.Left);
        monthText.fontStyle = FontStyles.Bold;
        monthText.textWrappingMode = TextWrappingModes.NoWrap;
        // Up to the "All months" button.
        statsText = UIKit.Text(root, "", UIKit.SmallSize, UIKit.MutedTextColor, Pad + 198f, Pad + 44f, 372f, 26f, TextAlignmentOptions.Left);

        float right = Size.x - Pad;
        UIKit.Button(root, "", Close, right - 52f, Pad + 6f, 52f, 52f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Close);
        speedButton = UIKit.Button(root, "1 mo/s", CycleSpeed, right - 52f - 12f - 96f, Pad + 6f, 96f, 52f, UIKit.ButtonStyle.Secondary,
                                   UIKit.SmallSize);
        allButton = UIKit.Button(root, "All months", () => { SetPlaying(false); graph.SetPeriod(-1); },
                                 right - 52f - 12f - 96f - 12f - 170f, Pad + 6f, 170f, 52f, UIKit.ButtonStyle.Secondary, UIKit.BodySize);

        // Bottom: chart of flights per day in the whole network, the slider on top of it, year ticks below.
        int n = graph.PeriodCount;
        MetaData meta = graph.Meta;
        perDay = new float[n];
        for (int i = 0; i < n; i++) perDay[i] = meta.periodDays[i] > 0 ? (float)meta.periodFlights[i] / meta.periodDays[i] : 0f;

        float chartX = Pad + 10f;
        float chartY = 108f;
        float chartW = Size.x - 2f * (Pad + 10f);
        float chartH = 74f;
        Image chartBack = UIKit.Card(root, Pad, chartY - 8f, Size.x - 2f * Pad, chartH + 16f, UIKit.SurfaceColor);
        chartBack.name = "Chart Background";

        RectTransform chartRect = UIKit.NewRect("Chart", root);
        UIKit.Place(chartRect, chartX, chartY, chartW, chartH);
        chart = chartRect.gameObject.AddComponent<UIChart>();
        chart.color = UIKit.AccentColor;
        chart.highlightColor = UIKit.HighlightColor;
        chart.axisColor = new Color(1f, 1f, 1f, 0f);
        chart.lineWidth = 3f;
        chart.raycastTarget = false;
        chart.SetData(perDay, UIChart.Kind.Line, -1);

        slider = BuildSlider(root, chartX, chartY - 8f, chartW, chartH + 16f, n);

        for (int i = 0; i < n; i++)
        {
            string period = meta.periods[i];
            if (period.Length < 7 || period.Substring(5, 2) != "01") continue;
            float x = chartX + (n > 1 ? chartW * i / (n - 1) : 0f);
            UIKit.Text(root, period.Substring(0, 4), UIKit.SmallSize, UIKit.MutedTextColor, x - 40f, chartY + chartH + 12f, 80f, 24f,
                       TextAlignmentOptions.Top);
        }

        UpdateSpeedLabel();
        SyncFromGraph();
    }

    /// <summary>
    /// Transparent slider over the chart: only its handle (a thin bar with a knob) is
    /// drawn; dragging or clicking anywhere on the chart sets the month.
    /// </summary>
    private Slider BuildSlider(Transform root, float x, float y, float w, float h, int n)
    {
        RectTransform rect = UIKit.NewRect("Slider", root);
        UIKit.Place(rect, x, y, w, h);
        Image hitArea = rect.gameObject.AddComponent<Image>();
        hitArea.color = new Color(1f, 1f, 1f, 0f); // invisible, but catches rays and clicks
        Slider s = rect.gameObject.AddComponent<Slider>();
        s.direction = Slider.Direction.LeftToRight;
        s.minValue = 0;
        s.maxValue = Mathf.Max(0, n - 1);
        s.wholeNumbers = true;
        s.transition = Selectable.Transition.None;
        Navigation nav = s.navigation;
        nav.mode = Navigation.Mode.None;
        s.navigation = nav;

        // Handle slide area keeps the handle centre on the chart's x range.
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
            graph.SetPeriod(Mathf.RoundToInt(value));
        });
        return s;
    }

    private void CycleSpeed()
    {
        if (speeds == null || speeds.Length == 0) return;
        speedIndex = (Mathf.Clamp(speedIndex, 0, speeds.Length - 1) + 1) % speeds.Length;
        UpdateSpeedLabel();
    }

    private void UpdateSpeedLabel()
    {
        if (speedButton == null) return;
        UIKit.SetButtonLabel(speedButton, CurrentSpeed.ToString("0.#", Inv) + " mo/s");
    }

    /// <summary>Updates texts, chart marker and slider from GraphLoader.Period (also when others change it).</summary>
    private void SyncFromGraph()
    {
        if (canvas == null) return;
        MetaData meta = graph.Meta;
        int n = graph.PeriodCount;
        if (graph.HasPeriod)
        {
            int p = graph.Period;
            monthText.text = MonthTitle(meta.periods[p]);
            float first = perDay[0];
            string share = first > 0f
                ? "  ·  " + (perDay[p] / first * 100f).ToString("0", Inv) + "% of " + ShortMonth(meta.periods[0])
                : "";
            statsText.text = perDay[p].ToString("N0", Inv) + " flights / day in total" + share;
            slider.SetValueWithoutNotify(p);
            slider.handleRect.gameObject.SetActive(true);
            chart.SetData(perDay, UIChart.Kind.Line, p);
            UIKit.SetButtonColor(allButton, UIKit.StyleColor(UIKit.ButtonStyle.Secondary));
        }
        else
        {
            monthText.text = "All months";
            statsText.text = ShortMonth(meta.periods[0]) + " to " + ShortMonth(meta.periods[n - 1]) + "  ·  " + n + " months together";
            slider.handleRect.gameObject.SetActive(false);
            chart.SetData(perDay, UIChart.Kind.Line, -1);
            UIKit.SetButtonColor(allButton, UIKit.AccentSoftColor);
        }
    }

    /// <summary>"2020-04" -> "April 2020".</summary>
    private static string MonthTitle(string period)
    {
        if (period != null && period.Length >= 7 && int.TryParse(period.Substring(5, 2), out int month) && month >= 1 && month <= 12)
        {
            return MonthNames[month - 1] + " " + period.Substring(0, 4);
        }
        return period;
    }

    /// <summary>"2020-04" -> "Apr 2020".</summary>
    private static string ShortMonth(string period)
    {
        string title = MonthTitle(period);
        int space = title != null ? title.IndexOf(' ') : -1;
        return space > 3 ? title.Substring(0, 3) + title.Substring(space) : title;
    }
}
