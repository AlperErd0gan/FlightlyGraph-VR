using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// A world-space dashboard that explains the loaded dataset with tables and charts,
/// opened / closed with the left controller's menu button (F1 in the Editor).
/// Layout: a sidebar (dataset, navigation in two groups, Timeline and Tour buttons)
/// and a page with a title, a short description and cards. Pages: Overview (key
/// figures, flights per day over time, lowest / highest / latest month), Insights,
/// Airports and Routes (top 10, rows select the airport / show the route), Traffic
/// mix, Clusters (communities, view-mode switch), Selected (charts of the selected
/// airport, or two airports side by side once one is pinned), Find (paged airport
/// list by name, country or traffic) and Filters (market segment, country, minimum
/// flights). Lists follow the filters and the timeline's month. Everything is built
/// at runtime (uGUI + TextMeshPro + UIKit / UIChart), no prefab. Pages whose data the
/// file lacks (e.g. OpenFlights has no time axis) say so instead. Buttons are pressed
/// with the XR ray (trigger / pinch) or by poking.
/// </summary>
public class DataDashboard : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Optional; the Clusters page switches views through it. Found in the scene if unset.")]
    public GraphViewMode viewMode;
    [Tooltip("Optional; hidden while the dashboard is open (the Selected page shows the same). Found in the scene if unset.")]
    public GraphInfoPanel infoPanel;
    [Tooltip("Optional; adds a Tour button to the sidebar. Found in the scene if unset.")]
    public GuidedTour tour;
    [Tooltip("Timeline (time slider) opened from the sidebar. Found in the scene if unset; added to this object if the scene has none.")]
    public TimelinePanel timeline;
    [Tooltip("Selecting an airport while the dashboard is open switches to the Selected page.")]
    public bool followSelection = true;
    [Tooltip("Picking an airport (Find, Airports, Insights) turns you (the XR Origin) to face it.")]
    public bool turnToSelection = true;
    public bool startOpen = false;

    [Header("Panel")]
    [Tooltip("Distance in front of the eyes when opened (m).")]
    public float distance = 1.15f;
    [Tooltip("Height of the panel centre relative to the eyes when opened (m).")]
    public float heightOffset = -0.1f;

    [Header("Follow the user")]
    [Tooltip("Lazily follow the head: stays put while you read, glides back in front when you turn or walk away.")]
    public bool followView = true;
    [Tooltip("Starts following once the panel is this many degrees (horizontally) away from where you look.")]
    public float followAngle = 40f;
    [Tooltip("Starts following once its place in front of you is this far away (walking) (m).")]
    public float followDistance = 0.4f;
    [Tooltip("Beyond this (e.g. after a teleport) the panel jumps instead of gliding (m).")]
    public float snapDistance = 2f;
    [Tooltip("Follow smoothing; higher = snappier.")]
    public float followSpeed = 3f;

    [Header("UI sounds")]
    public bool uiSounds = true;
    [Tooltip("Optional clips. Empty = short tones generated at runtime (no asset needed).")]
    public AudioClip clickSound;
    public AudioClip hoverSound;
    public AudioClip openSound;
    public AudioClip closeSound;
    [Range(0f, 1f)] public float soundVolume = 0.5f;

    public enum Tab { Overview, Insights, Airports, Routes, TrafficMix, Clusters, Selected, Find, Filters }
    private enum FindSort { Name, Country, Traffic }

    // Layout (canvas units, 1000 = 1 m). Fixed on purpose: the panel is designed as a whole.
    private static readonly Vector2 Size = new Vector2(1060f, 700f);
    private const float SideWidth = 236f;
    private const float Pad = 28f;
    private const float HeaderHeight = 116f;
    private const float RowHeight = 30f;
    private const int FindRowsPerPage = 9;

    private static readonly Tab[] ExploreTabs = { Tab.Overview, Tab.Insights, Tab.Airports, Tab.Routes, Tab.TrafficMix, Tab.Clusters };
    private static readonly Tab[] ToolTabs = { Tab.Selected, Tab.Find, Tab.Filters };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] MonthShort = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private Canvas canvas;
    private RectTransform content;
    private TextMeshProUGUI pageTitle;
    private TextMeshProUGUI pageSubtitle;
    private TextMeshProUGUI periodChip;
    private Image periodChipBack;
    private TextMeshProUGUI datasetText;
    private readonly Dictionary<Tab, NavItem> nav = new Dictionary<Tab, NavItem>();
    private Tab current = Tab.Overview;
    private GraphNode pinned;            // compared with the selected airport on the Selected page
    private List<GraphInsight> insights; // computed once (GraphInsights)
    private FindSort findSort = FindSort.Name;
    private int findPage;
    private InputAction toggleAction;
    private bool following;

    private class NavItem
    {
        public Button button;
        public Image indicator;
        public TextMeshProUGUI label;
    }

    private MetaData meta => graph != null && graph.HasTimeAxis ? graph.Meta : null;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (viewMode == null) viewMode = FindFirstObjectByType<GraphViewMode>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (infoPanel == null) infoPanel = FindFirstObjectByType<GraphInfoPanel>();
        if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
        if (timeline == null) timeline = gameObject.AddComponent<TimelinePanel>();

        toggleAction = new InputAction("Toggle Dashboard", InputActionType.Button);
        toggleAction.AddBinding("<XRController>{LeftHand}/{MenuButton}");
        toggleAction.AddBinding("<Keyboard>/f1");
    }

    private void Start()
    {
        UIKit.EnsureXREventSystem();
        UISounds.Enabled = uiSounds;
        UISounds.Volume = soundVolume;
        UISounds.SetClips(clickSound, hoverSound, openSound, closeSound);
        BuildPanel();
        canvas.gameObject.SetActive(false);
        if (startOpen) StartCoroutine(OpenWhenLoaded());
    }

    private void OnEnable()
    {
        toggleAction.Enable();
        if (selector != null) selector.NodeSelected += OnNodeSelected;
        if (graph != null) graph.PeriodChanged += OnPeriodChanged;
    }

    private void OnDisable()
    {
        toggleAction.Disable();
        if (selector != null) selector.NodeSelected -= OnNodeSelected;
        if (graph != null) graph.PeriodChanged -= OnPeriodChanged;
    }

    private void OnDestroy()
    {
        toggleAction.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    private void Update()
    {
        // During the guided tour the tour opens / closes the dashboard; its buttons are inert.
        bool locked = GuidedTour.InputLocked;
        if (canvas != null && IsOpen)
        {
            foreach (BaseRaycaster raycaster in canvas.GetComponents<BaseRaycaster>()) raycaster.enabled = !locked;
        }
        if (locked) return;
        if (toggleAction.WasPressedThisFrame())
        {
            if (IsOpen) Close();
            else Open();
        }
    }

    private void LateUpdate()
    {
        if (!followView || !IsOpen) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Transform head = cam.transform;
        Vector3 forward = UIKit.FlatForward(head);
        Vector3 targetPosition = head.position + forward * distance + Vector3.up * heightOffset;
        Transform panel = canvas.transform;
        float offset = Vector3.Distance(panel.position, targetPosition);

        if (offset > snapDistance)
        {
            PlaceInFront(head);
            following = false;
            return;
        }

        Vector3 toPanel = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
        float angle = toPanel.sqrMagnitude > 1e-6f ? Vector3.Angle(forward, toPanel) : 0f;
        if (!following && (offset > followDistance || angle > followAngle)) following = true;
        if (!following) return;

        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        panel.position = Vector3.Lerp(panel.position, targetPosition, t);
        Vector3 away = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
        if (away.sqrMagnitude > 1e-6f)
        {
            // Stays upright and keeps facing the user while it moves.
            panel.rotation = Quaternion.Slerp(panel.rotation, Quaternion.LookRotation(away, Vector3.up), t);
        }
        if (Vector3.Distance(panel.position, targetPosition) < 0.03f) following = false;
    }

    private void PlaceInFront(Transform head)
    {
        Vector3 forward = UIKit.FlatForward(head);
        canvas.transform.position = head.position + forward * distance + Vector3.up * heightOffset;
        // A world-space canvas reads correctly when its +Z points away from the viewer.
        canvas.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;

    public void Open()
    {
        if (graph == null || !graph.IsLoaded) return;
        Camera cam = Camera.main;
        if (cam != null)
        {
            PlaceInFront(cam.transform);
            canvas.worldCamera = cam;
        }
        following = false;
        canvas.gameObject.SetActive(true);
        if (infoPanel != null) infoPanel.SetSuppressed(true);
        UISounds.Play(UISounds.Open, canvas.transform.position);
        UpdateDatasetText();
        Show(current);
    }

    public void Close()
    {
        if (canvas == null) return;
        UISounds.Play(UISounds.Close, canvas.transform.position);
        canvas.gameObject.SetActive(false);
        if (infoPanel != null) infoPanel.SetSuppressed(false);
    }

    /// <summary>Opens the dashboard (if closed) on the given page (used by the guided tour).</summary>
    public void OpenTab(Tab tab)
    {
        current = tab;
        if (!IsOpen) Open();
        else Show(tab);
    }

    /// <summary>The time axis (meta file), or null when the dataset has none.</summary>
    public MetaData TimeAxis => meta;

    private IEnumerator OpenWhenLoaded()
    {
        while (graph == null || !graph.IsLoaded) yield return null;
        Open();
    }

    private void OnNodeSelected(GraphNode node)
    {
        if (IsOpen && (current == Tab.Selected || followSelection)) Show(Tab.Selected);
    }

    private void OnPeriodChanged()
    {
        // Lists and charts follow the timeline's month.
        if (IsOpen) Show(current);
    }

    // ---- Frame -----------------------------------------------------------

    private void BuildPanel()
    {
        canvas = UIKit.WorldCanvas("DataDashboard", Size);
        Transform root = canvas.transform;
        Image background = UIKit.Box(root, "Background", UIKit.PanelColor, UIKit.PanelRadius);
        UIKit.Stretch(background.rectTransform);

        // Sidebar: dataset, navigation, actions.
        Image side = UIKit.Box(root, "Sidebar", new Color(1f, 1f, 1f, 0.03f), UIKit.PanelRadius);
        UIKit.Place(side.rectTransform, 0f, 0f, SideWidth, Size.y);
        TextMeshProUGUI title = UIKit.Text(root, "Flight data", 34f, UIKit.TextColor, Pad, Pad, SideWidth - Pad, 44f);
        title.fontStyle = FontStyles.Bold;
        datasetText = UIKit.Text(root, "", 17f, UIKit.MutedTextColor, Pad, Pad + 44f, SideWidth - Pad - 10f, 70f);
        datasetText.lineSpacing = -8f;

        float y = 150f;
        y = NavGroup(root, "EXPLORE", ExploreTabs, y);
        NavGroup(root, "TOOLS", ToolTabs, y + 10f);
        if (timeline != null)
        {
            UIKit.Button(root, "Timeline", OpenTimeline, 20f, Size.y - Pad - 52f, SideWidth - 40f, 52f,
                         UIKit.ButtonStyle.Primary, UIKit.BodySize, UIIcon.Shape.Timeline);
        }

        // Page header: title, description, timeline month, tour, close.
        float x = SideWidth + Pad;
        float w = Size.x - x - Pad;
        float right = x + w - 52f;
        UIKit.Button(root, "", Close, right, Pad, 52f, 52f, UIKit.ButtonStyle.Ghost, 22f, UIIcon.Shape.Close);
        if (tour != null)
        {
            right -= 10f + 150f;
            UIKit.Button(root, "Tour", () => { Close(); tour.StartTour(); }, right, Pad + 4f, 150f, 44f,
                         UIKit.ButtonStyle.Secondary, UIKit.SmallSize, UIIcon.Shape.Play);
        }
        right -= 12f + 200f;
        periodChipBack = UIKit.Box(root, "Period Chip", new Color(1f, 0.6f, 0.2f, 0.16f), 14f);
        UIKit.Place(periodChipBack.rectTransform, right, Pad + 8f, 200f, 36f);
        periodChip = UIKit.Text(periodChipBack.transform, "", 17f, UIKit.HighlightColor, TextAlignmentOptions.Center);
        UIKit.Stretch(periodChip.rectTransform);
        pageTitle = UIKit.Text(root, "", 36f, UIKit.TextColor, x, Pad - 2f, right - x - 12f, 46f);
        pageTitle.fontStyle = FontStyles.Bold;
        pageSubtitle = UIKit.Text(root, "", UIKit.SmallSize, UIKit.MutedTextColor, x, Pad + 46f, w - 20f, 28f);

        content = UIKit.NewRect("Content", root);
        UIKit.Place(content, x, HeaderHeight, w, Size.y - HeaderHeight - Pad);
    }

    private float NavGroup(Transform root, string caption, Tab[] tabs, float y)
    {
        UIKit.Text(root, caption, 15f, UIKit.MutedTextColor, Pad, y, SideWidth - Pad, 22f).characterSpacing = 8f;
        y += 26f;
        foreach (Tab tab in tabs)
        {
            Tab t = tab;
            Button button = UIKit.Button(root, PageName(tab), () => Show(t), 14f, y, SideWidth - 28f, 40f,
                                         UIKit.ButtonStyle.Ghost, UIKit.BodySize);
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Left;
            label.margin = new Vector4(22f, 0f, 8f, 0f);
            Image indicator = UIKit.Box(button.transform, "Indicator", UIKit.AccentColor, 2f);
            indicator.rectTransform.anchorMin = new Vector2(0f, 0.2f);
            indicator.rectTransform.anchorMax = new Vector2(0f, 0.8f);
            indicator.rectTransform.pivot = new Vector2(0f, 0.5f);
            indicator.rectTransform.sizeDelta = new Vector2(4f, 0f);
            indicator.rectTransform.anchoredPosition = new Vector2(6f, 0f);
            nav[tab] = new NavItem { button = button, indicator = indicator, label = label };
            y += 42f;
        }
        return y;
    }

    private void OpenTimeline()
    {
        Close();
        timeline.Open();
    }

    private void UpdateDatasetText()
    {
        if (datasetText == null || graph == null || !graph.IsLoaded) return;
        string text = graph.Nodes.Count + " airports\n" + Num(graph.Edges.Count) + " routes";
        if (meta != null) text += "\n" + MonthLabel(meta.periods[0]) + " to " + MonthLabel(meta.periods[meta.periods.Length - 1]);
        datasetText.text = text;
    }

    private static string PageName(Tab tab)
    {
        switch (tab)
        {
            case Tab.TrafficMix: return "Traffic mix";
            case Tab.Selected: return "Selected airport";
            case Tab.Find: return "Find airport";
            default: return tab.ToString();
        }
    }

    private string PageDescription(Tab tab)
    {
        switch (tab)
        {
            case Tab.Overview: return "The whole network at a glance";
            case Tab.Insights: return "Facts found automatically in the data · tap one to see it";
            case Tab.Airports: return "Busiest airports in the current view · tap a row to go there";
            case Tab.Routes: return "Busiest routes in the current view · tap a row to show it";
            case Tab.TrafficMix: return "Market segments and hours of the day";
            case Tab.Clusters: return "Groups of airports that mostly fly to each other (Louvain communities)";
            case Tab.Selected: return pinned != null ? "Two airports side by side" : "Charts of the airport you selected";
            case Tab.Find: return "Pick an airport to select it and turn towards it";
            case Tab.Filters: return "Show part of the network; airports outside it are dimmed";
            default: return "";
        }
    }

    private void Show(Tab tab)
    {
        current = tab;
        foreach (KeyValuePair<Tab, NavItem> kv in nav)
        {
            bool active = kv.Key == tab;
            UIKit.SetButtonColor(kv.Value.button, active ? UIKit.AccentSoftColor : UIKit.StyleColor(UIKit.ButtonStyle.Ghost));
            kv.Value.indicator.gameObject.SetActive(active);
            kv.Value.label.color = active ? UIKit.AccentColor : UIKit.TextColor;
            kv.Value.label.fontStyle = active ? FontStyles.Bold : FontStyles.Normal;
        }
        pageTitle.text = PageName(tab);
        pageSubtitle.text = PageDescription(tab);
        bool month = graph != null && graph.HasPeriod;
        periodChipBack.gameObject.SetActive(month);
        if (month) periodChip.text = "Timeline: " + MonthLabel(graph.PeriodName(graph.Period));

        for (int i = content.childCount - 1; i >= 0; i--) Destroy(content.GetChild(i).gameObject);
        if (graph == null || !graph.IsLoaded) return;

        switch (tab)
        {
            case Tab.Overview: BuildOverview(); break;
            case Tab.Insights: BuildInsights(); break;
            case Tab.Airports: BuildAirports(); break;
            case Tab.Routes: BuildRoutes(); break;
            case Tab.TrafficMix: BuildTrafficMix(); break;
            case Tab.Clusters: BuildClusters(); break;
            case Tab.Selected: BuildSelected(); break;
            case Tab.Find: BuildFind(); break;
            case Tab.Filters: BuildFilters(); break;
        }
    }

    private float W => content.rect.width;
    private float H => content.rect.height;

    // ---- Overview --------------------------------------------------------

    private void BuildOverview()
    {
        const float gap = 14f;
        long flights = 0;
        if (meta != null) flights = meta.totalFlights;
        else foreach (GraphNode node in graph.Nodes.Values) flights += node.value;
        string[] values =
        {
            graph.Nodes.Count.ToString(Inv), Num(graph.Edges.Count), Big(meta != null ? meta.totalFlights : flights / 2),
            meta != null ? meta.periods.Length.ToString(Inv) : "-",
        };
        string[] labels = { "airports", "routes", meta != null ? "flights" : "flights (sum of airports / 2)", "months" };
        float kpiWidth = (W - 3f * gap) / 4f;
        for (int i = 0; i < 4; i++) Kpi(i * (kpiWidth + gap), 0f, kpiWidth, 92f, values[i], labels[i]);

        if (meta == null)
        {
            Body("This dataset has no time axis, so there is no traffic-over-time chart.\n" +
                 "The other pages still describe it.", 0f, 120f, W, 80f);
            return;
        }

        int n = meta.periods.Length;
        var perDay = new float[n];
        int low = 0;
        int peak = 0;
        for (int i = 0; i < n; i++)
        {
            perDay[i] = meta.periodDays[i] > 0 ? (float)meta.periodFlights[i] / meta.periodDays[i] : 0f;
            if (perDay[i] < perDay[low]) low = i;
            if (perDay[i] > perDay[peak]) peak = i;
        }
        int mark = graph.HasPeriod ? graph.Period : low;
        ChartCard("Flights per day in the whole network", perDay, UIChart.Kind.Line, 0f, 106f, W, 300f, mark,
                  MonthLabel(meta.periods[0]), MonthLabel(meta.periods[n - 1]), Num(perDay[peak]) + " / day max");
        if (timeline != null)
        {
            UIKit.Button(content, "Play over time", () => { OpenTimeline(); timeline.SetPlaying(true); }, W - 16f - 200f, 118f, 200f, 40f,
                         UIKit.ButtonStyle.Primary, UIKit.SmallSize, UIIcon.Shape.Play);
        }

        float first = perDay[0];
        string Detail(int i) => Num(perDay[i]) + " / day" + (first > 0 ? "  ·  " + (perDay[i] / first * 100f).ToString("0", Inv) + "%" : "");
        string vs = " (vs " + MonthLabel(meta.periods[0]) + ")";
        float statWidth = (W - 2f * gap) / 3f;
        Stat(0f, 420f, statWidth, "Lowest month" + vs, MonthLabel(meta.periods[low]), Detail(low), UIKit.BadColor);
        Stat(statWidth + gap, 420f, statWidth, "Highest month" + vs, MonthLabel(meta.periods[peak]), Detail(peak), UIKit.GoodColor);
        Stat(2f * (statWidth + gap), 420f, statWidth, "Latest month" + vs, MonthLabel(meta.periods[n - 1]), Detail(n - 1), UIKit.AccentColor);
    }

    // ---- Airports / Routes -----------------------------------------------

    private void BuildAirports()
    {
        var nodes = new List<GraphNode>();
        foreach (GraphNode node in graph.Nodes.Values)
        {
            if (graph.NodeInFocus(node) && graph.DisplayValue(node) > 0) nodes.Add(node);
        }
        nodes.Sort((a, b) =>
        {
            int c = graph.DisplayValue(b).CompareTo(graph.DisplayValue(a));
            return c != 0 ? c : string.CompareOrdinal(a.id, b.id);
        });
        int n = Mathf.Min(10, nodes.Count);
        bool rich = n > 0 && nodes[0].data != null && nodes[0].data.departures + nodes[0].data.arrivals > 0;

        float y = ViewNote(0f);
        if (n == 0)
        {
            Body("No airport matches the current filter.", 0f, y, W, 40f);
            return;
        }
        var rows = new List<string[]>();
        var actions = new List<UnityAction>();
        var values = new float[n];
        var codes = new string[n];
        for (int i = 0; i < n; i++)
        {
            GraphNode node = nodes[i];
            values[i] = graph.DisplayValue(node);
            codes[i] = node.ShortCode;
            rows.Add(new[]
            {
                (i + 1).ToString(Inv), "<b>" + node.ShortCode + "</b>  " + Trim(ShortName(node), 30),
                node.data != null ? Trim(node.data.country, 16) : "", Big(graph.DisplayValue(node)),
                rich ? Pct(node.data.cargoShare) : "-",
            });
            GraphNode target = node;
            actions.Add(() => FocusAirport(target));
        }
        y = Table(0f, y, W, new[] { "#", "Airport", "Country", FlightsHeader(), "Cargo" },
                  new[] { 0f, 0.06f, 0.56f, 0.76f, 0.9f }, rows, actions);
        BarsCard(values, codes, 0f, y + 12f, W, H - y - 12f);
    }

    private void BuildRoutes()
    {
        var edges = new List<GraphEdge>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (graph.PassesFilter(e) && graph.FilteredWeight(e) > 0) edges.Add(e);
        }
        edges.Sort((a, b) =>
        {
            int c = graph.FilteredWeight(b).CompareTo(graph.FilteredWeight(a));
            return c != 0 ? c : a.index.CompareTo(b.index);
        });
        int n = Mathf.Min(10, edges.Count);

        float y = ViewNote(0f);
        if (n == 0)
        {
            Body("No route matches the current filter.", 0f, y, W, 40f);
            return;
        }
        var rows = new List<string[]>();
        var actions = new List<UnityAction>();
        var values = new float[n];
        var names = new string[n];
        for (int i = 0; i < n; i++)
        {
            GraphEdge e = edges[i];
            GraphNode a = graph.Nodes[e.sourceId];
            GraphNode b = graph.Nodes[e.targetId];
            values[i] = graph.FilteredWeight(e);
            names[i] = a.ShortCode + "-" + b.ShortCode;
            rows.Add(new[]
            {
                (i + 1).ToString(Inv), "<b>" + names[i] + "</b>", Trim(City(a), 16) + " - " + Trim(City(b), 16),
                Big(graph.FilteredWeight(e)), Num(GreatCircleKm(a, b)) + " km",
            });
            GraphEdge target = e;
            actions.Add(() => ShowRoute(target));
        }
        y = Table(0f, y, W, new[] { "#", "Route", "Cities", FlightsHeader(), "Distance" },
                  new[] { 0f, 0.06f, 0.24f, 0.66f, 0.82f }, rows, actions);
        BarsCard(values, names, 0f, y + 12f, W, H - y - 12f);
    }

    /// <summary>Column title; with the timeline on one month the counts are that month's (named in the header chip).</summary>
    private string FlightsHeader()
    {
        return graph.HasPeriod ? "In month" : "Flights";
    }

    /// <summary>A line naming the active filter (segment / country / minimum); returns the y below it.</summary>
    private float ViewNote(float y)
    {
        if (!graph.HasFilter) return y;
        var parts = new List<string>();
        if (graph.FilterSegment != null) parts.Add(graph.FilterSegment);
        if (graph.FilterCountry != null) parts.Add(graph.FilterCountry);
        if (graph.FilterMinWeight > 0) parts.Add(MinWeightLabel(graph.FilterMinWeight));
        UIKit.Text(content, "Filter: " + string.Join(" · ", parts), UIKit.SmallSize, UIKit.HighlightColor, 0f, y, W, 26f);
        return y + 30f;
    }

    // ---- Traffic mix -----------------------------------------------------

    private void BuildTrafficMix()
    {
        var segments = new Dictionary<string, long>();
        var hourly = new float[24];
        bool hasHours = false;
        long total = 0;
        foreach (GraphEdge e in graph.Edges)
        {
            if (e.data == null) continue;
            if (e.data.segments != null)
            {
                foreach (KeyValuePair<string, int> kv in e.data.segments)
                {
                    segments.TryGetValue(kv.Key, out long sum);
                    segments[kv.Key] = sum + kv.Value;
                    total += kv.Value;
                }
            }
            if (e.data.hourly != null && e.data.hourly.Length == 24)
            {
                hasHours = true;
                for (int h = 0; h < 24; h++) hourly[h] += e.data.hourly[h];
            }
        }
        if (total == 0 && !hasHours)
        {
            Body("This dataset has no market segments or hours of day.", 0f, 0f, W, 60f);
            return;
        }

        const float gap = 18f;
        float half = (W - gap) / 2f;
        if (total > 0)
        {
            var list = new List<KeyValuePair<string, long>>(segments);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var rows = new List<string[]>();
            for (int i = 0; i < list.Count; i++)
            {
                rows.Add(new[] { list[i].Key, Pct((float)list[i].Value / total) });
            }
            Heading("Market segments", 0f, 0f, half);
            Table(0f, 36f, half, new[] { "Segment", "Share of flights" }, new[] { 0f, 0.62f }, rows, null, 36f);
        }
        if (hasHours)
        {
            int peak = 0;
            for (int h = 1; h < 24; h++) if (hourly[h] > hourly[peak]) peak = h;
            float x = total > 0 ? half + gap : 0f;
            float w = total > 0 ? half : W;
            Heading("Departures by hour (UTC)", x, 0f, w);
            ChartCard("Busiest " + peak.ToString("00", Inv) + ":00-" + ((peak + 1) % 24).ToString("00", Inv) + ":00 UTC",
                      hourly, UIChart.Kind.Bars, x, 36f, w, Mathf.Min(H - 36f, 420f), peak, "00:00", "23:00", "");
        }
    }

    // ---- Clusters --------------------------------------------------------

    private void BuildClusters()
    {
        IReadOnlyList<int> communities = graph.Communities;
        if (communities.Count <= 1)
        {
            Body("This dataset has no clusters (no `community` field; use a nodes_3d file).", 0f, 0f, W, 60f);
            return;
        }
        long total = 0;
        foreach (GraphNode node in graph.Nodes.Values) total += node.value;

        var rows = new List<string[]>();
        var shares = new float[communities.Count];
        var names = new string[communities.Count];
        for (int i = 0; i < communities.Count; i++)
        {
            int c = communities[i];
            long sum = 0;
            foreach (GraphNode node in graph.CommunityMembers(c)) sum += node.value;
            shares[i] = total > 0 ? (float)sum / total : 0f;
            names[i] = (c + 1).ToString(Inv);
            string hex = ColorUtility.ToHtmlStringRGB(graph.CommunityColor(c));
            // <mark> paints a colour swatch behind the number (no special glyph needed in the font).
            rows.Add(new[]
            {
                "<mark=#" + hex + "CC> " + (c + 1) + " </mark>", graph.CommunityName(c),
                graph.CommunityMembers(c).Count.ToString(Inv), Pct(shares[i]),
            });
        }
        float y = Table(0f, 0f, W, new[] { "Cluster", "Biggest airports", "Airports", "Traffic" },
                        new[] { 0f, 0.14f, 0.64f, 0.82f }, rows, null);
        BarsCard(shares, names, 0f, y + 12f, W * 0.62f, H - y - 12f);
        bool regional = graph.RegionalView;
        float bx = W * 0.62f + 18f;
        Body(regional ? "The graph shows the regional view." : "The graph shows the top routes.", bx, y + 24f, W - bx, 60f);
        UIKit.Button(content, regional ? "Show top routes" : "Show regional view", ToggleView, bx, y + 84f, W - bx, 52f,
                     UIKit.ButtonStyle.Primary, UIKit.BodySize, UIIcon.Shape.Grid);
    }

    private void ToggleView()
    {
        bool toRegional = !graph.RegionalView;
        if (viewMode != null)
        {
            viewMode.Apply(toRegional ? GraphViewMode.ViewMode.Regional : GraphViewMode.ViewMode.TopRoutes, true);
        }
        else
        {
            if (selector != null) selector.ClearSelection();
            graph.SetRegionalView(toRegional);
        }
        Show(Tab.Clusters);
    }

    // ---- Selected / comparison ------------------------------------------

    private void BuildSelected()
    {
        GraphNode node = selector != null ? selector.SelectedNode : null;
        if (node == null)
        {
            Body(pinned != null
                ? "<b>" + pinned.label + "</b> is pinned. Select another airport to compare them."
                : "Select an airport (trigger / pinch, or a row in Airports / Find) to see its charts here.", 0f, 0f, W - 260f, 70f);
            if (pinned != null)
            {
                UIKit.Button(content, "Unpin", () => { pinned = null; Show(Tab.Selected); }, W - 220f, 0f, 220f, 46f);
            }
            return;
        }

        // Pin / unpin for a side-by-side comparison.
        if (pinned == null)
        {
            UIKit.Button(content, "Pin to compare", () => { pinned = node; Show(Tab.Selected); }, W - 220f, 0f, 220f, 46f);
        }
        else
        {
            UIKit.Button(content, "Unpin " + pinned.ShortCode, () => { pinned = null; Show(Tab.Selected); }, W - 220f, 0f, 220f, 46f);
            if (pinned != node)
            {
                BuildComparison(pinned, node);
                return;
            }
        }

        NodeData d = node.data;
        string place = Place(node);
        TextMeshProUGUI name = UIKit.Text(content, node.label, 28f, UIKit.TextColor, 0f, 0f, W - 240f, 38f);
        name.fontStyle = FontStyles.Bold;
        string sub = place + (place.Length > 0 ? "  ·  " : "") + Big(node.value) + " flights";
        if (graph.HasPeriod && d != null && d.monthly != null && graph.Period < d.monthly.Length)
        {
            sub += "  ·  " + Num(d.monthly[graph.Period]) + " in " + MonthLabel(graph.PeriodName(graph.Period));
        }
        UIKit.Text(content, sub, UIKit.SmallSize, UIKit.MutedTextColor, 0f, 40f, W - 240f, 26f);

        const float gap = 18f;
        float half = (W - gap) / 2f;
        bool hasMonths = d != null && d.monthly != null && d.monthly.Length > 0;
        float top = 82f;
        float chartHeight = 330f;
        if (hasMonths)
        {
            float[] perDay = PerDaySeries(node);
            int n = perDay.Length;
            int low = 0;
            for (int i = 0; i < n; i++) if (perDay[i] < perDay[low]) low = i;
            bool days = meta != null && meta.periodDays.Length == n;
            int mark = graph.HasPeriod && graph.Period < n ? graph.Period : low;
            ChartCard(days ? "Flights per day, by month" : "Flights by period", perDay, UIChart.Kind.Line, 0f, top, half, chartHeight, mark,
                      days ? MonthLabel(meta.periods[0]) : "first", days ? MonthLabel(meta.periods[n - 1]) : "last",
                      Num(Max(perDay)) + " / day max");
        }
        bool hasHours = d != null && d.hourly != null && d.hourly.Length == 24;
        if (hasHours)
        {
            var hours = new float[24];
            int peak = 0;
            for (int h = 0; h < 24; h++)
            {
                hours[h] = d.hourly[h];
                if (hours[h] > hours[peak]) peak = h;
            }
            float x = hasMonths ? half + gap : 0f;
            ChartCard("Flights by hour (UTC)", hours, UIChart.Kind.Bars, x, top, hasMonths ? half : W, chartHeight, peak, "00", "23",
                      "busiest " + peak.ToString("00", Inv) + ":00");
        }
        if (!hasMonths && !hasHours)
        {
            Body("This dataset has no monthly or hourly counts for airports.", 0f, top, W, 60f);
        }
        if (d != null && d.segments != null && d.segments.Count > 0)
        {
            var list = new List<KeyValuePair<string, int>>(d.segments);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            long sum = 0;
            foreach (KeyValuePair<string, int> kv in list) sum += kv.Value;
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < 3; i++)
            {
                if (i > 0) sb.Append("   ·   ");
                sb.Append(list[i].Key).Append(" <b>").Append(Pct((float)list[i].Value / sum)).Append("</b>");
            }
            float y = top + chartHeight + 14f;
            UIKit.Card(content, 0f, y, W, 56f);
            UIKit.Text(content, "Segments", UIKit.SmallSize, UIKit.MutedTextColor, 18f, y + 16f, 120f, 26f);
            UIKit.Text(content, sb.ToString(), UIKit.BodySize, UIKit.TextColor, 130f, y + 14f, W - 150f, 30f);
        }
    }

    /// <summary>
    /// Two airports side by side: traffic as a share of each one's first month (so a small
    /// and a big airport compare on the same scale, e.g. recovery after COVID), plus a table.
    /// </summary>
    private void BuildComparison(GraphNode a, GraphNode b)
    {
        string colorA = ColorUtility.ToHtmlStringRGB(UIKit.AccentColor);
        string colorB = ColorUtility.ToHtmlStringRGB(UIKit.HighlightColor);
        UIKit.Text(content, "<color=#" + colorA + "><b>" + a.ShortCode + "</b></color> " + Trim(ShortName(a), 18) + "   vs   " +
                            "<color=#" + colorB + "><b>" + b.ShortCode + "</b></color> " + Trim(ShortName(b), 18),
                   26f, UIKit.TextColor, 0f, 4f, W - 240f, 40f);

        float[] seriesA = PerDaySeries(a);
        float[] seriesB = PerDaySeries(b);
        if (seriesA != null && seriesB != null && seriesA.Length == seriesB.Length && seriesA.Length > 1 && seriesA[0] > 0 && seriesB[0] > 0)
        {
            int n = seriesA.Length;
            var indexA = new float[n];
            var indexB = new float[n];
            for (int i = 0; i < n; i++)
            {
                indexA[i] = seriesA[i] / seriesA[0] * 100f;
                indexB[i] = seriesB[i] / seriesB[0] * 100f;
            }
            string from = meta != null ? MonthLabel(meta.periods[0]) : "first";
            string to = meta != null ? MonthLabel(meta.periods[n - 1]) : "last";
            float chartWidth = W * 0.54f;
            UIChart chart = ChartCard("Traffic as % of " + from, indexA, UIChart.Kind.Line, 0f, 62f, chartWidth, 380f,
                                      -1, from, to, "100% = " + from);
            chart.SetSecondSeries(indexB, UIKit.HighlightColor);

            int lowA = Lowest(indexA);
            int lowB = Lowest(indexB);
            var rows = new List<string[]>
            {
                new[] { "Flights", Big(a.value), Big(b.value) },
                new[] { "Lowest", indexA[lowA].ToString("0", Inv) + "%", indexB[lowB].ToString("0", Inv) + "%" },
                new[] { "Lowest in", PeriodLabel(lowA), PeriodLabel(lowB) },
                new[] { "90% again", RecoveryLabel(indexA), RecoveryLabel(indexB) },
                new[] { "Latest", indexA[n - 1].ToString("0", Inv) + "%", indexB[n - 1].ToString("0", Inv) + "%" },
                new[] { "Highest", Max(indexA).ToString("0", Inv) + "%", Max(indexB).ToString("0", Inv) + "%" },
            };
            float x = chartWidth + 18f;
            Table(x, 62f, W - x, new[] { "", "<color=#" + colorA + "><b>" + a.ShortCode + "</b></color>", "<color=#" + colorB + "><b>" + b.ShortCode + "</b></color>" },
                  new[] { 0f, 0.4f, 0.7f }, rows, null, 54f);
        }
        else
        {
            Body(a.ShortCode + ": " + Big(a.value) + " flights\n" + b.ShortCode + ": " + Big(b.value) + " flights\n" +
                 "(no monthly data in this dataset for a traffic-over-time comparison)", 0f, 70f, W, 150f);
        }
    }

    /// <summary>Flights per day for each period (flights per period without a time axis), or null.</summary>
    private float[] PerDaySeries(GraphNode node)
    {
        NodeData d = node.data;
        if (d == null || d.monthly == null || d.monthly.Length == 0) return null;
        int n = d.monthly.Length;
        bool days = meta != null && meta.periodDays.Length == n;
        var series = new float[n];
        for (int i = 0; i < n; i++)
        {
            series[i] = days && meta.periodDays[i] > 0 ? (float)d.monthly[i] / meta.periodDays[i] : d.monthly[i];
        }
        return series;
    }

    private static int Lowest(float[] index)
    {
        int low = 0;
        for (int i = 1; i < index.Length; i++) if (index[i] < index[low]) low = i;
        return low;
    }

    /// <summary>First period after the lowest one where traffic is back to 90% of the first period.</summary>
    private string RecoveryLabel(float[] index)
    {
        int low = Lowest(index);
        for (int i = low; i < index.Length; i++)
        {
            if (index[i] >= 90f) return PeriodLabel(i);
        }
        return "not yet";
    }

    private string PeriodLabel(int i)
    {
        return meta != null && i < meta.periods.Length ? MonthLabel(meta.periods[i]) : "#" + (i + 1);
    }

    // ---- Find ------------------------------------------------------------

    private void BuildFind()
    {
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        CompareInfo compare = Inv.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        switch (findSort)
        {
            case FindSort.Name:
                nodes.Sort((a, b) => compare.Compare(ShortName(a), ShortName(b), options));
                break;
            case FindSort.Country:
                nodes.Sort((a, b) =>
                {
                    int c = compare.Compare(CountryName(a), CountryName(b), options);
                    return c != 0 ? c : compare.Compare(ShortName(a), ShortName(b), options);
                });
                break;
            default:
                nodes.Sort((a, b) => a.value != b.value ? b.value.CompareTo(a.value) : string.CompareOrdinal(a.id, b.id));
                break;
        }

        // Sort toggles.
        Toggles(new List<string> { "By name", "By country", "By traffic" }, 0f, 0f, 480f, 42f, i =>
            ((FindSort)i == findSort, () => { findSort = (FindSort)i; findPage = 0; Show(Tab.Find); }));

        // Jump buttons: first letters of names or countries (traffic has no index).
        float listTop = 54f;
        if (findSort != FindSort.Traffic)
        {
            var letters = new List<char>();
            var firstIndex = new Dictionary<char, int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                char letter = Initial(findSort == FindSort.Name ? ShortName(nodes[i]) : CountryName(nodes[i]));
                if (!firstIndex.ContainsKey(letter))
                {
                    firstIndex[letter] = i;
                    letters.Add(letter);
                }
            }
            float letterWidth = Mathf.Min(38f, (W - 4f * (letters.Count - 1)) / Mathf.Max(1, letters.Count));
            int currentPage = findPage;
            for (int i = 0; i < letters.Count; i++)
            {
                int page = firstIndex[letters[i]] / FindRowsPerPage;
                Button button = UIKit.Button(content, letters[i].ToString(), () => { findPage = page; Show(Tab.Find); },
                                             i * (letterWidth + 4f), 54f, letterWidth, 36f, UIKit.ButtonStyle.Ghost, 17f);
                if (page == currentPage) UIKit.SetButtonColor(button, UIKit.AccentSoftColor);
            }
            listTop = 100f;
        }

        int pages = Mathf.Max(1, Mathf.CeilToInt(nodes.Count / (float)FindRowsPerPage));
        findPage = Mathf.Clamp(findPage, 0, pages - 1);
        for (int row = 0; row < FindRowsPerPage; row++)
        {
            int i = findPage * FindRowsPerPage + row;
            if (i >= nodes.Count) break;
            GraphNode node = nodes[i];
            string place = findSort == FindSort.Country ? CountryName(node) : Place(node);
            string text = "<b>" + node.ShortCode + "</b>   " + Trim(ShortName(node), 38) + "   <size=80%><color=#9BA3B4>" + place +
                          " · " + Big(node.value) + "</color></size>";
            bool selected = selector != null && selector.SelectedNode == node;
            Button button = UIKit.Button(content, text, () => FocusAirport(node), 0f, listTop + row * 40f, W, 36f,
                                         UIKit.ButtonStyle.Secondary, 19f);
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Left;
            label.margin = new Vector4(16f, 0f, 16f, 0f);
            if (selected) UIKit.SetButtonColor(button, UIKit.AccentSoftColor);
        }

        float pagerY = listTop + FindRowsPerPage * 40f + 8f;
        UIKit.Button(content, "Prev", () => { findPage--; Show(Tab.Find); }, 0f, pagerY, 140f, 42f, UIKit.ButtonStyle.Secondary,
                     UIKit.BodySize, UIIcon.Shape.Previous).interactable = findPage > 0;
        UIKit.Text(content, "Page " + (findPage + 1) + " / " + pages + "  ·  " + nodes.Count + " airports", UIKit.SmallSize,
                   UIKit.MutedTextColor, 150f, pagerY + 10f, W - 300f, 28f, TextAlignmentOptions.Top);
        UIKit.Button(content, "Next", () => { findPage++; Show(Tab.Find); }, W - 140f, pagerY, 140f, 42f, UIKit.ButtonStyle.Secondary,
                     UIKit.BodySize, UIIcon.Shape.Next).interactable = findPage < pages - 1;
    }

    /// <summary>Selects the airport and, if enabled, turns the XR Origin to face it.</summary>
    private void FocusAirport(GraphNode node)
    {
        if (selector != null) selector.SelectNode(node);
        TurnToward(node.transform.position);
    }

    /// <summary>Closes the dashboard (the info panel describes the route), selects the route and turns to it.</summary>
    private void ShowRoute(GraphEdge edge)
    {
        if (selector == null) return;
        Close();
        selector.SelectEdge(edge);
        TurnToward(edge.Midpoint);
    }

    private void TurnToward(Vector3 point)
    {
        if (!turnToSelection) return;
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = Camera.main;
        if (origin == null || cam == null) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        Vector3 toPoint = Vector3.ProjectOnPlane(point - cam.transform.position, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f || toPoint.sqrMagnitude < 1e-6f) return;
        origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(forward, toPoint, Vector3.up));
        // Keep the dashboard in front after the turn.
        if (IsOpen) PlaceInFront(cam.transform);
    }

    // ---- Insights --------------------------------------------------------

    private void BuildInsights()
    {
        if (insights == null) insights = GraphInsights.Compute(graph, meta);
        if (insights.Count == 0)
        {
            Body("No insights for this dataset: they need a monthly time axis or market segments.", 0f, 0f, W, 60f);
            return;
        }

        // Categories in order, each placed in the shorter of two columns.
        var categories = new List<string>();
        foreach (GraphInsight insight in insights)
        {
            if (!categories.Contains(insight.category)) categories.Add(insight.category);
        }
        const float gap = 18f;
        float columnWidth = (W - gap) / 2f;
        var columnY = new float[2];
        foreach (string category in categories)
        {
            int column = columnY[0] <= columnY[1] ? 0 : 1;
            float x = column * (columnWidth + gap);
            Heading(category, x, columnY[column], columnWidth, 20f);
            columnY[column] += 30f;
            foreach (GraphInsight insight in insights)
            {
                if (insight.category != category) continue;
                GraphInsight target = insight;
                Button button = UIKit.Button(content, insight.text, () => OpenInsight(target), x, columnY[column], columnWidth, 36f,
                                             UIKit.ButtonStyle.Secondary, 18f);
                TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
                text.alignment = TextAlignmentOptions.Left;
                text.margin = new Vector4(14f, 0f, 10f, 0f);
                text.enableAutoSizing = true;
                text.fontSizeMin = 13f;
                text.fontSizeMax = 18f;
                columnY[column] += 40f;
            }
            columnY[column] += 12f;
        }
        float noteY = Mathf.Max(columnY[0], columnY[1]) + 2f;
        UIKit.Text(content, "Season-neutral: growth compares the same months of the latest year with the first months of the data; " +
                            "recovery uses 12-month averages. An airport opening or another one closing nearby shows up as growth.",
                   15f, UIKit.MutedTextColor, 0f, noteY, W, 50f);
    }

    /// <summary>Airport: select it and turn to it (the Selected page shows its charts). Route: close the dashboard and show it.</summary>
    private void OpenInsight(GraphInsight insight)
    {
        if (insight.node != null) FocusAirport(insight.node);
        else if (insight.edge != null) ShowRoute(insight.edge);
    }

    private static char Initial(string s)
    {
        if (string.IsNullOrEmpty(s)) return '?';
        // "İstanbul" / "Ålesund" -> I / A: drop accents so letters group as expected.
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch);
        }
        return '?';
    }

    private static string CountryName(GraphNode node)
    {
        return node.data != null && !string.IsNullOrEmpty(node.data.country) ? node.data.country : "?";
    }

    private static string Place(GraphNode node)
    {
        NodeData d = node.data;
        if (d == null) return "";
        if (!string.IsNullOrEmpty(d.city) && !string.IsNullOrEmpty(d.country)) return d.city + ", " + d.country;
        return !string.IsNullOrEmpty(d.city) ? d.city : d.country ?? "";
    }

    // ---- Filters ---------------------------------------------------------

    private void BuildFilters()
    {
        // Segments by total flights, countries by airport traffic.
        var segmentTotals = new Dictionary<string, long>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (e.data == null || e.data.segments == null) continue;
            foreach (KeyValuePair<string, int> kv in e.data.segments)
            {
                segmentTotals.TryGetValue(kv.Key, out long sum);
                segmentTotals[kv.Key] = sum + kv.Value;
            }
        }
        var countryTotals = new Dictionary<string, long>();
        foreach (GraphNode node in graph.Nodes.Values)
        {
            if (node.data == null || string.IsNullOrEmpty(node.data.country)) continue;
            countryTotals.TryGetValue(node.data.country, out long sum);
            countryTotals[node.data.country] = sum + node.value;
        }
        if (segmentTotals.Count == 0 && countryTotals.Count == 0)
        {
            Body("This dataset has no market segments or countries to filter by.", 0f, 0f, W, 60f);
            return;
        }

        float y = 0f;
        if (segmentTotals.Count > 0)
        {
            var segments = SortedKeys(segmentTotals, 7);
            Heading("Market segment", 0f, y, W);
            var labels = new List<string> { "All" };
            foreach (string seg in segments) labels.Add(Abbreviate(seg));
            Toggles(labels, 0f, y + 34f, W, 42f, i =>
            {
                string seg = i == 0 ? null : segments[i - 1];
                return (i == 0 ? graph.FilterSegment == null : graph.FilterSegment == seg,
                        () => ApplyFilter(seg, graph.FilterCountry, graph.FilterMinWeight));
            });
            y += 100f;
        }
        if (countryTotals.Count > 0)
        {
            var countries = SortedKeys(countryTotals, 11);
            Heading("Country", 0f, y, W);
            UIKit.Text(content, "the biggest by traffic", UIKit.SmallSize, UIKit.MutedTextColor, 110f, y + 4f, 300f, 24f);
            var labels = new List<string> { "All" };
            foreach (string country in countries) labels.Add(Trim(country, 14));
            Toggles(labels.GetRange(0, Mathf.Min(6, labels.Count)), 0f, y + 34f, W, 42f, i =>
            {
                string country = i == 0 ? null : countries[i - 1];
                return (i == 0 ? graph.FilterCountry == null : graph.FilterCountry == country,
                        () => ApplyFilter(graph.FilterSegment, country, graph.FilterMinWeight));
            });
            if (labels.Count > 6)
            {
                Toggles(labels.GetRange(6, labels.Count - 6), 0f, y + 84f, W, 42f, i =>
                {
                    string country = countries[i + 5];
                    return (graph.FilterCountry == country, () => ApplyFilter(graph.FilterSegment, country, graph.FilterMinWeight));
                });
            }
            y += 150f;
        }

        Heading("Minimum flights per route", 0f, y, W);
        int[] thresholds = MinWeightPresets();
        var thresholdLabels = new List<string>();
        foreach (int t in thresholds) thresholdLabels.Add(t == 0 ? "All" : MinWeightLabel(t));
        Toggles(thresholdLabels, 0f, y + 34f, W, 42f, i =>
        {
            int t = thresholds[i];
            return (graph.FilterMinWeight == t, () => ApplyFilter(graph.FilterSegment, graph.FilterCountry, t));
        });
        y += 100f;

        int visibleEdges = 0;
        foreach (GraphEdge e in graph.Edges) if (e.visible) visibleEdges++;
        int focusNodes = 0;
        foreach (GraphNode node in graph.Nodes.Values) if (graph.NodeInFocus(node)) focusNodes++;
        UIKit.Card(content, 0f, y, W, 64f);
        Body("Showing <b>" + visibleEdges + "</b> routes and <b>" + focusNodes + "</b> airports in focus", 20f, y + 18f, W - 280f, 32f);
        UIKit.Button(content, "Reset filters", () => ApplyFilter(null, null, 0), W - 220f, y + 10f, 204f, 44f,
                     UIKit.ButtonStyle.Primary, UIKit.BodySize);
    }

    private void ApplyFilter(string segment, string country, int minWeight)
    {
        // Highlights are drawn on top of the old colours: drop them before restyling.
        if (selector != null) selector.ClearSelection();
        graph.SetFilter(segment, country, minWeight);
        Show(Tab.Filters);
    }

    private static List<string> SortedKeys(Dictionary<string, long> totals, int max)
    {
        var list = new List<KeyValuePair<string, long>>(totals);
        list.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key));
        var keys = new List<string>();
        for (int i = 0; i < list.Count && i < max; i++) keys.Add(list[i].Key);
        return keys;
    }

    /// <summary>Thresholds in flights per day over the whole period when there is a time axis, else raw counts.</summary>
    private int[] MinWeightPresets()
    {
        if (meta != null && meta.days > 0)
        {
            float[] perDay = { 0f, 1f, 5f, 20f, 50f };
            var result = new int[perDay.Length];
            for (int i = 0; i < perDay.Length; i++) result[i] = Mathf.CeilToInt(perDay[i] * meta.days);
            return result;
        }
        return new[] { 0, 10, 100, 1000, 10000 };
    }

    private string MinWeightLabel(int minWeight)
    {
        if (meta != null && meta.days > 0) return (minWeight / (float)meta.days).ToString("0.#", Inv) + "+ / day";
        return Num(minWeight) + "+";
    }

    // ---- Building blocks -------------------------------------------------

    private TextMeshProUGUI Body(string text, float x, float y, float w, float h)
    {
        return UIKit.Text(content, text, UIKit.BodySize, UIKit.TextColor, x, y, w, h);
    }

    private TextMeshProUGUI Heading(string text, float x, float y, float w, float size = UIKit.BodySize)
    {
        TextMeshProUGUI t = UIKit.Text(content, text, size, UIKit.TextColor, x, y, w, 30f);
        t.fontStyle = FontStyles.Bold;
        return t;
    }

    /// <summary>Key figure card: a big number with a label under it.</summary>
    private void Kpi(float x, float y, float w, float h, string value, string label)
    {
        UIKit.Card(content, x, y, w, h);
        TextMeshProUGUI v = UIKit.Text(content, value, 34f, UIKit.TextColor, x + 18f, y + 12f, w - 30f, 44f);
        v.fontStyle = FontStyles.Bold;
        UIKit.Text(content, label, UIKit.SmallSize, UIKit.MutedTextColor, x + 18f, y + 56f, w - 30f, 26f);
    }

    /// <summary>Small card: caption, value in a colour, detail line.</summary>
    private void Stat(float x, float y, float w, string caption, string value, string detail, Color accent)
    {
        UIKit.Card(content, x, y, w, 96f);
        Image bar = UIKit.Box(content, "Accent", accent, 2f);
        UIKit.Place(bar.rectTransform, x + 10f, y + 16f, 4f, 64f);
        UIKit.Text(content, caption, 15f, UIKit.MutedTextColor, x + 26f, y + 12f, w - 36f, 24f);
        TextMeshProUGUI v = UIKit.Text(content, value, 26f, UIKit.TextColor, x + 26f, y + 34f, w - 36f, 34f);
        v.fontStyle = FontStyles.Bold;
        UIKit.Text(content, detail, 16f, UIKit.MutedTextColor, x + 26f, y + 66f, w - 36f, 24f);
    }

    /// <summary>
    /// Card with a title, an optional label at the top right, a line / bar chart and
    /// its first / last labels under it. Returns the chart (e.g. for a second series).
    /// </summary>
    private UIChart ChartCard(string title, float[] values, UIChart.Kind kind, float x, float y, float w, float h, int highlight,
                              string leftLabel, string rightLabel, string topRight)
    {
        UIKit.Card(content, x, y, w, h);
        bool wide = w >= 520f;
        bool hasTopRight = !string.IsNullOrEmpty(topRight);
        UIKit.Text(content, title, UIKit.SmallSize, UIKit.TextColor, x + 18f, y + 14f, wide && hasTopRight ? w * 0.6f : w - 36f, 26f)
             .fontStyle = FontStyles.Bold;
        float chartTop = y + 52f;
        if (hasTopRight)
        {
            if (wide)
            {
                UIKit.Text(content, topRight, 16f, UIKit.MutedTextColor, x + w * 0.5f, y + 16f, w * 0.5f - 18f, 24f, TextAlignmentOptions.TopRight);
            }
            else
            {
                // Narrow card: under the title instead of beside it.
                UIKit.Text(content, topRight, 16f, UIKit.MutedTextColor, x + 18f, y + 40f, w - 36f, 24f);
                chartTop = y + 72f;
            }
        }
        RectTransform rect = UIKit.NewRect("Chart", content);
        UIKit.Place(rect, x + 18f, chartTop, w - 36f, y + h - 40f - chartTop);
        UIChart chart = rect.gameObject.AddComponent<UIChart>();
        chart.color = UIKit.AccentColor;
        chart.highlightColor = UIKit.HighlightColor;
        chart.axisColor = new Color(1f, 1f, 1f, 0.15f);
        chart.raycastTarget = false;
        chart.SetData(values, kind, highlight);
        UIKit.Text(content, leftLabel, 16f, UIKit.MutedTextColor, x + 18f, y + h - 34f, w / 2f - 18f, 24f);
        UIKit.Text(content, rightLabel, 16f, UIKit.MutedTextColor, x + w / 2f, y + h - 34f, w / 2f - 18f, 24f, TextAlignmentOptions.TopRight);
        return chart;
    }

    /// <summary>Card with a bar chart and a short label under each bar (the first bar highlighted).</summary>
    private void BarsCard(float[] values, string[] labels, float x, float y, float w, float h)
    {
        if (h < 80f) return; // no room left
        UIKit.Card(content, x, y, w, h);
        RectTransform rect = UIKit.NewRect("Bars", content);
        UIKit.Place(rect, x + 16f, y + 14f, w - 32f, h - 14f - 34f);
        UIChart chart = rect.gameObject.AddComponent<UIChart>();
        chart.color = UIKit.AccentColor;
        chart.highlightColor = UIKit.HighlightColor;
        chart.axisColor = new Color(1f, 1f, 1f, 0.15f);
        chart.raycastTarget = false;
        chart.SetData(values, UIChart.Kind.Bars, 0);
        float slot = (w - 32f) / Mathf.Max(1, values.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            UIKit.Text(content, labels[i], 14f, UIKit.MutedTextColor, x + 16f + i * slot, y + h - 30f, slot, 24f, TextAlignmentOptions.Top)
                 .textWrappingMode = TextWrappingModes.NoWrap;
        }
    }

    /// <summary>
    /// Table in a card: a muted header row, then rows with alternating shading; with
    /// actions, each row is a button. columns are left edges as fractions of the width.
    /// Returns the y below the table.
    /// </summary>
    private float Table(float x, float y, float w, string[] headers, float[] columns, List<string[]> rows, List<UnityAction> actions,
                        float rowHeight = RowHeight)
    {
        float height = 34f + rows.Count * rowHeight + 10f;
        UIKit.Card(content, x, y, w, height);
        float inner = w - 24f;
        for (int c = 0; c < headers.Length; c++)
        {
            float cx = x + 12f + columns[c] * inner;
            float cw = (c + 1 < columns.Length ? columns[c + 1] : 1f) * inner - columns[c] * inner;
            UIKit.Text(content, headers[c], 16f, UIKit.MutedTextColor, cx + 8f, y + 10f, cw - 8f, 22f);
        }
        float ry = y + 34f;
        for (int r = 0; r < rows.Count; r++)
        {
            Transform parent = content;
            float offsetX = x + 12f;
            float offsetY = ry;
            if (actions != null && r < actions.Count && actions[r] != null)
            {
                Button row = UIKit.Button(content, "", actions[r], x + 8f, ry, w - 16f, rowHeight - 2f, UIKit.ButtonStyle.Ghost);
                if (r % 2 == 0) UIKit.SetButtonColor(row, new Color(1f, 1f, 1f, 0.035f));
                parent = row.transform;
                offsetX = 4f;
                offsetY = 0f;
            }
            else if (r % 2 == 0)
            {
                Image stripe = UIKit.Box(content, "Stripe", new Color(1f, 1f, 1f, 0.035f), 8f);
                UIKit.Place(stripe.rectTransform, x + 8f, ry, w - 16f, rowHeight - 2f);
            }
            for (int c = 0; c < rows[r].Length && c < columns.Length; c++)
            {
                float cx = offsetX + columns[c] * inner;
                float cw = (c + 1 < columns.Length ? columns[c + 1] : 1f) * inner - columns[c] * inner;
                TextMeshProUGUI cell = UIKit.Text(parent, rows[r][c], 19f, UIKit.TextColor, cx + 8f, offsetY + 3f, cw - 8f, rowHeight - 4f,
                                                  TextAlignmentOptions.Left);
                cell.textWrappingMode = TextWrappingModes.NoWrap;
            }
            ry += rowHeight;
        }
        return y + height;
    }

    /// <summary>A row of equal toggle buttons; setup(i) returns whether button i is active and its action.</summary>
    private void Toggles(List<string> labels, float x, float y, float w, float h, System.Func<int, (bool active, UnityAction action)> setup)
    {
        const float gap = 8f;
        float width = (w - gap * (labels.Count - 1)) / labels.Count;
        for (int i = 0; i < labels.Count; i++)
        {
            (bool active, UnityAction action) = setup(i);
            Button button = UIKit.Button(content, labels[i], action, x + i * (width + gap), y, width, h,
                                         active ? UIKit.ButtonStyle.Primary : UIKit.ButtonStyle.Secondary, 18f);
            TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
            text.enableAutoSizing = true;
            text.fontSizeMin = 13f;
            text.fontSizeMax = 18f;
        }
    }

    // ---- Formatting ------------------------------------------------------

    private static string Num(float v) => v.ToString("N0", Inv);

    private static string Pct(float share) => (share * 100f).ToString("0", Inv) + "%";

    /// <summary>44,388,968 -> "44.4 M"; 215,132 -> "215 k"; smaller numbers in full.</summary>
    private static string Big(long v)
    {
        if (v >= 1000000) return (v / 1000000f).ToString("0.0", Inv) + " M";
        if (v >= 10000) return (v / 1000f).ToString("0", Inv) + " k";
        return v.ToString("N0", Inv);
    }

    /// <summary>"2020-04" -> "Apr 2020".</summary>
    private static string MonthLabel(string period)
    {
        if (period != null && period.Length >= 7 && int.TryParse(period.Substring(5, 2), out int month) && month >= 1 && month <= 12)
        {
            return MonthShort[month - 1] + " " + period.Substring(0, 4);
        }
        return period ?? "";
    }

    private static float Max(float[] values)
    {
        float max = 0f;
        foreach (float v in values) max = Mathf.Max(max, v);
        return max;
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 2) + "..";
    }

    private static string City(GraphNode node)
    {
        return node.data != null && !string.IsNullOrEmpty(node.data.city) ? node.data.city : node.ShortCode;
    }

    /// <summary>Airport name without the trailing "(IATA)" code.</summary>
    private static string ShortName(GraphNode node)
    {
        string label = node.label ?? node.id;
        int paren = label.LastIndexOf(" (", System.StringComparison.Ordinal);
        return paren > 0 ? label.Substring(0, paren) : label;
    }

    /// <summary>Short labels for market segments.</summary>
    private static string Abbreviate(string segment)
    {
        switch (segment)
        {
            case "Mainline": return "Main";
            case "Regional Aircraft": return "Regional";
            case "All-Cargo": return "Cargo";
            case "Lowcost": return "Low-cost";
            case "Business Aviation": return "Business";
            case "Other Types": return "Other";
            case "Traditional Scheduled": return "Trad. sched.";
            default: return segment.Length > 10 ? segment.Substring(0, 8) + ".." : segment;
        }
    }

    private static float GreatCircleKm(GraphNode a, GraphNode b)
    {
        const float earthRadiusKm = 6371f;
        float p1 = a.lat * Mathf.Deg2Rad;
        float p2 = b.lat * Mathf.Deg2Rad;
        float dp = (b.lat - a.lat) * Mathf.Deg2Rad;
        float dl = (b.lon - a.lon) * Mathf.Deg2Rad;
        float h = Mathf.Sin(dp / 2f) * Mathf.Sin(dp / 2f) +
                  Mathf.Cos(p1) * Mathf.Cos(p2) * Mathf.Sin(dl / 2f) * Mathf.Sin(dl / 2f);
        return 2f * earthRadiusKm * Mathf.Asin(Mathf.Min(1f, Mathf.Sqrt(h)));
    }
}
