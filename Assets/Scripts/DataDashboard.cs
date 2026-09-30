using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// A world-space dashboard that explains the loaded dataset with tables and charts,
/// opened / closed with the left controller's menu button (F1 in the Editor).
/// Tabs: Overview (size, period, flights per day over time), Airports (top 10),
/// Routes (top 10), Traffic mix (market segments, flights per UTC hour), Clusters
/// (communities, view-mode switch), Selected (charts of the selected airport, or
/// two airports side by side once one is pinned), Find (paged airport list by name,
/// country or traffic; picking one selects it and turns you towards it) and Filters
/// (market segment, country, minimum flights; Airports / Routes follow the filter).
/// Everything is built at runtime (uGUI + TextMeshPro + UIChart), no prefab. Pages
/// whose data the file lacks (e.g. OpenFlights has no time axis) say so instead.
/// Buttons are pressed with the XR ray (trigger / pinch) or by poking.
/// </summary>
public class DataDashboard : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Optional; the Clusters tab switches views through it. Found in the scene if unset.")]
    public GraphViewMode viewMode;
    [Tooltip("Optional; hidden while the dashboard is open (the Selected tab shows the same). Found in the scene if unset.")]
    public GraphInfoPanel infoPanel;
    [Tooltip("Optional; adds a Tour button to the header. Found in the scene if unset.")]
    public GuidedTour tour;
    [Tooltip("Selecting an airport while the dashboard is open switches to the Selected tab.")]
    public bool followSelection = true;
    [Tooltip("Picking an airport in the Find tab turns you (the XR Origin) to face it.")]
    public bool turnToSelection = true;
    [Tooltip("Time axis file in StreamingAssets (periods, flights per period). Missing = no time charts.")]
    public string metaFileName = "meta_ectrl.json";
    public bool startOpen = false;

    [Header("Panel")]
    [Tooltip("Distance in front of the eyes when opened (m).")]
    public float distance = 1.1f;
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
    [Tooltip("Panel size in canvas units; 1000 units = 1 m.")]
    public Vector2 size = new Vector2(1000f, 680f);
    public Color panelColor = new Color(0.06f, 0.07f, 0.1f, 0.9f);
    public Color accentColor = new Color(0.3f, 0.75f, 1f, 1f);
    public Color highlightColor = new Color(1f, 0.55f, 0.15f, 1f);
    public Color tabColor = new Color(0.16f, 0.18f, 0.24f, 1f);
    public Color tabActiveColor = new Color(0.22f, 0.45f, 0.65f, 1f);

    [Header("UI sounds")]
    public bool uiSounds = true;
    [Tooltip("Optional clips. Empty = short tones generated at runtime (no asset needed).")]
    public AudioClip clickSound;
    public AudioClip hoverSound;
    public AudioClip openSound;
    public AudioClip closeSound;
    [Range(0f, 1f)] public float soundVolume = 0.5f;

    public enum Tab { Overview, Airports, Routes, TrafficMix, Clusters, Selected, Find, Filters }
    private enum FindSort { Name, Country, Traffic }

    private static readonly string[] TabNames = { "Overview", "Airports", "Routes", "Traffic mix", "Clusters", "Selected", "Find", "Filters" };
    private const int FindRowsPerPage = 9;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const float Pad = 30f;

    private Canvas canvas;
    private RectTransform content;
    private readonly List<Image> tabImages = new List<Image>();
    private Tab current = Tab.Overview;
    private GraphNode pinned;          // compared with the selected airport in the Selected tab
    private FindSort findSort = FindSort.Name;
    private int findPage;
    private MetaData meta;
    private InputAction toggleAction;
    private bool following;
    private AudioSource uiAudio;
    private readonly List<AudioClip> generatedClips = new List<AudioClip>();

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (viewMode == null) viewMode = FindFirstObjectByType<GraphViewMode>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (infoPanel == null) infoPanel = FindFirstObjectByType<GraphInfoPanel>();

        toggleAction = new InputAction("Toggle Dashboard", InputActionType.Button);
        toggleAction.AddBinding("<XRController>{LeftHand}/{MenuButton}");
        toggleAction.AddBinding("<Keyboard>/f1");
    }

    private void Start()
    {
        EnsureEventSystem();
        BuildSounds();
        BuildPanel();
        canvas.gameObject.SetActive(false);
        StartCoroutine(LoadMeta());
        if (startOpen) StartCoroutine(OpenWhenLoaded());
    }

    private void OnEnable()
    {
        toggleAction.Enable();
        if (selector != null) selector.NodeSelected += OnNodeSelected;
    }

    private void OnDisable()
    {
        toggleAction.Disable();
        if (selector != null) selector.NodeSelected -= OnNodeSelected;
    }

    private void OnDestroy()
    {
        toggleAction.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
        if (uiAudio != null) Destroy(uiAudio.gameObject);
        foreach (AudioClip clip in generatedClips) Destroy(clip);
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
        Vector3 forward = FlatForward(head);
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

    private static Vector3 FlatForward(Transform head)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // looking straight down / up
        return forward.normalized;
    }

    private void PlaceInFront(Transform head)
    {
        Vector3 forward = FlatForward(head);
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
        PlaySound(openSound);
        Show(current);
    }

    public void Close()
    {
        PlaySound(closeSound);
        canvas.gameObject.SetActive(false);
        if (infoPanel != null) infoPanel.SetSuppressed(false);
    }

    // ---- Sounds ----------------------------------------------------------

    /// <summary>
    /// A separate, always-active audio object (the canvas is deactivated on close, which
    /// would cut the close sound). Missing clips are replaced by short generated tones.
    /// </summary>
    private void BuildSounds()
    {
        uiAudio = new GameObject("DataDashboard Audio").AddComponent<AudioSource>();
        uiAudio.playOnAwake = false;
        uiAudio.spatialBlend = 1f;   // from the panel
        uiAudio.minDistance = 3f;    // full volume within reach
        if (clickSound == null) clickSound = Tone("UI Click", 1400f, 1400f, 0.04f, 0.9f);
        if (hoverSound == null) hoverSound = Tone("UI Hover", 2200f, 2200f, 0.015f, 0.35f);
        if (openSound == null) openSound = Tone("UI Open", 600f, 1200f, 0.09f, 0.8f);
        if (closeSound == null) closeSound = Tone("UI Close", 1200f, 600f, 0.09f, 0.8f);
    }

    private void PlaySound(AudioClip clip)
    {
        if (!uiSounds || uiAudio == null || clip == null) return;
        uiAudio.transform.position = canvas.transform.position;
        uiAudio.PlayOneShot(clip, soundVolume);
    }

    /// <summary>Short sine tone gliding from startHz to endHz with a fast decay (a soft UI blip).</summary>
    private AudioClip Tone(string name, float startHz, float endHz, float seconds, float amplitude)
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
        generatedClips.Add(clip);
        return clip;
    }

    private IEnumerator OpenWhenLoaded()
    {
        while (graph == null || !graph.IsLoaded) yield return null;
        Open();
    }

    private void OnNodeSelected(GraphNode node)
    {
        if (IsOpen && (current == Tab.Selected || followSelection)) Show(Tab.Selected);
    }

    // ---- Setup -----------------------------------------------------------

    /// <summary>World-space UI in XR needs an EventSystem with the XR UI Input Module.</summary>
    private static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            eventSystem = new GameObject("EventSystem (XR)").AddComponent<EventSystem>();
        }
        if (eventSystem.GetComponent<XRUIInputModule>() == null)
        {
            foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>()) module.enabled = false;
            eventSystem.gameObject.AddComponent<XRUIInputModule>();
        }
    }

    private IEnumerator LoadMeta()
    {
        if (string.IsNullOrEmpty(metaFileName)) yield break;
        string path = Path.Combine(Application.streamingAssetsPath, metaFileName);
        string uri = path.Contains("://") ? path : "file://" + path;
        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"DataDashboard: no time axis ({metaFileName} not found); time charts are hidden.");
                yield break;
            }
            try
            {
                meta = JsonConvert.DeserializeObject<MetaData>(request.downloadHandler.text);
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"DataDashboard: could not read {metaFileName}: {ex.Message}");
            }
        }
        if (IsOpen) Show(current);
    }

    private void BuildPanel()
    {
        var root = new GameObject("DataDashboard", typeof(RectTransform));
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays and poke
        root.AddComponent<GraphicRaycaster>();              // mouse in the Editor
        var rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = size;
        rootRect.localScale = Vector3.one * 0.001f;          // 1000 units = 1 m

        Image background = NewImage("Background", rootRect, panelColor);
        Stretch(background.rectTransform);

        Label(rootRect, "<b>Flight data</b>", 34, Pad, 18, size.x - 200, 50);
        MakeButton(rootRect, "Close", size.x - Pad - 120, 18, 120, 46, Close, tabColor);
        if (tour != null)
        {
            MakeButton(rootRect, "Tour", size.x - Pad - 250, 18, 120, 46, () => { Close(); tour.StartTour(); }, tabActiveColor);
        }

        float tabWidth = (size.x - 2 * Pad - (TabNames.Length - 1) * 8f) / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            Tab tab = (Tab)i;
            Button button = MakeButton(rootRect, TabNames[i], Pad + i * (tabWidth + 8f), 80, tabWidth, 50, () => Show(tab), tabColor);
            TextMeshProUGUI tabText = button.GetComponentInChildren<TextMeshProUGUI>();
            tabText.enableAutoSizing = true; // eight tabs: long names shrink to fit
            tabText.fontSizeMin = 14f;
            tabText.fontSizeMax = 22f;
            tabImages.Add((Image)button.targetGraphic);
        }

        content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
        content.SetParent(rootRect, false);
        Place(content, Pad, 150, size.x - 2 * Pad, size.y - 170);
    }

    // ---- Pages -----------------------------------------------------------

    /// <summary>Opens the dashboard (if closed) on the given tab (used by the guided tour).</summary>
    public void OpenTab(Tab tab)
    {
        current = tab;
        if (!IsOpen) Open();
        else Show(tab);
    }

    /// <summary>The time axis (meta file), or null when the dataset has none.</summary>
    public MetaData TimeAxis => HasTimeAxis() ? meta : null;

    private void Show(Tab tab)
    {
        current = tab;
        for (int i = 0; i < tabImages.Count; i++) tabImages[i].color = i == (int)tab ? tabActiveColor : tabColor;
        for (int i = content.childCount - 1; i >= 0; i--) Destroy(content.GetChild(i).gameObject);
        if (graph == null || !graph.IsLoaded) return;

        switch (tab)
        {
            case Tab.Overview: BuildOverview(); break;
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

    private void BuildOverview()
    {
        var sb = new StringBuilder();
        sb.Append("Airports <b>").Append(graph.Nodes.Count).Append("</b>    Routes <b>").Append(Num(graph.Edges.Count)).Append("</b>");
        if (meta != null && meta.periods != null && meta.periods.Length > 0)
        {
            sb.Append("    Flights <b>").Append(Big(meta.totalFlights)).Append("</b>    Period <b>")
              .Append(meta.periods[0]).Append(" - ").Append(meta.periods[meta.periods.Length - 1])
              .Append("</b> (").Append(meta.periods.Length).Append(meta.periods.Length == 1 ? " month)" : " months)");
        }
        Label(content, sb.ToString(), 24, 0, 0, W, 40);

        if (!HasTimeAxis())
        {
            Label(content, "This dataset has no time axis, so there is no traffic-over-time chart.\n" +
                           "Graph size and the other tabs still describe it.", 22, 0, 60, W, 80);
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
        Label(content, "Flights per day in the whole network (all airports), by month", 22, 0, 50, W, 34);
        ChartWithAxis(perDay, UIChart.Kind.Line, 0, 90, W, 300, low, meta.periods[0], meta.periods[n - 1], $"{Num(perDay[peak])} / day");

        float first = perDay[0];
        string share(int i) => first > 0 ? $" ({perDay[i] / first * 100f:0}% of {meta.periods[0]})" : "";
        Label(content,
              $"Lowest: <b>{meta.periods[low]}</b>, {Num(perDay[low])} flights/day{share(low)}\n" +
              $"Highest: <b>{meta.periods[peak]}</b>, {Num(perDay[peak])} flights/day{share(peak)}\n" +
              $"Latest: <b>{meta.periods[n - 1]}</b>, {Num(perDay[n - 1])} flights/day{share(n - 1)}",
              22, 0, 420, W, 100);
    }

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

        var sb = new StringBuilder(FilterNote());
        sb.Append("<b>#<pos=5%>Airport<pos=52%>Country<pos=74%>Flights<pos=90%>Cargo</b>\n");
        var values = new float[n];
        var codes = new string[n];
        for (int i = 0; i < n; i++)
        {
            GraphNode node = nodes[i];
            values[i] = graph.DisplayValue(node);
            codes[i] = node.ShortCode;
            sb.Append(i + 1).Append("<pos=5%>").Append(node.ShortCode).Append("  ").Append(Trim(ShortName(node), 26))
              .Append("<pos=52%>").Append(node.data != null ? Trim(node.data.country, 16) : "")
              .Append("<pos=74%>").Append(Big(graph.DisplayValue(node)))
              .Append("<pos=90%>").Append(rich ? Pct(node.data.cargoShare) : "-").Append('\n');
        }
        if (n == 0) sb.Append("No airport matches the current filter.");
        Label(content, sb.ToString(), 20, 0, 0, W, 310);
        BarsWithLabels(values, codes, 0, 320, W, 170);
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

        var sb = new StringBuilder(FilterNote());
        sb.Append("<b>#<pos=5%>Route<pos=22%>Cities<pos=66%>Flights<pos=80%>Distance</b>\n");
        var values = new float[n];
        var names = new string[n];
        for (int i = 0; i < n; i++)
        {
            GraphEdge e = edges[i];
            GraphNode a = graph.Nodes[e.sourceId];
            GraphNode b = graph.Nodes[e.targetId];
            values[i] = graph.FilteredWeight(e);
            names[i] = a.ShortCode + "-" + b.ShortCode;
            sb.Append(i + 1).Append("<pos=5%>").Append(names[i])
              .Append("<pos=22%>").Append(Trim(City(a), 16)).Append(" - ").Append(Trim(City(b), 16))
              .Append("<pos=66%>").Append(Big(graph.FilteredWeight(e)))
              .Append("<pos=80%>").Append(Num(GreatCircleKm(a, b))).Append(" km\n");
        }
        if (n == 0) sb.Append("No route matches the current filter.");
        Label(content, sb.ToString(), 20, 0, 0, W, 310);
        BarsWithLabels(values, names, 0, 320, W, 170);
    }

    /// <summary>"Filter: Cargo · Germany · 5+ / day" line, or empty when nothing is filtered.</summary>
    private string FilterNote()
    {
        if (!graph.HasFilter) return "";
        var parts = new List<string>();
        if (graph.FilterSegment != null) parts.Add(graph.FilterSegment);
        if (graph.FilterCountry != null) parts.Add(graph.FilterCountry);
        if (graph.FilterMinWeight > 0) parts.Add(MinWeightLabel(graph.FilterMinWeight));
        return "<color=#FFB060>Filter: " + string.Join(" · ", parts) + "</color>\n";
    }

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
            Label(content, "This dataset has no market segments or hours of day.", 22, 0, 0, W, 60);
            return;
        }

        float half = (W - 40f) / 2f;
        if (total > 0)
        {
            var list = new List<KeyValuePair<string, long>>(segments);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var sb = new StringBuilder("<b>Market segment<pos=70%>Share</b>\n");
            var shares = new float[list.Count];
            var names = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                shares[i] = (float)list[i].Value / total;
                names[i] = Abbreviate(list[i].Key);
                sb.Append(list[i].Key).Append("<pos=70%>").Append(Pct(shares[i])).Append('\n');
            }
            Label(content, "Flights between graph airports by market segment", 22, 0, 0, half, 34);
            Label(content, sb.ToString(), 20, 0, 40, half, 250);
            BarsWithLabels(shares, names, 0, 300, half, 170);
        }
        if (hasHours)
        {
            int peak = 0;
            for (int h = 1; h < 24; h++) if (hourly[h] > hourly[peak]) peak = h;
            float x = total > 0 ? half + 40f : 0f;
            float w = total > 0 ? half : W;
            Label(content, "Departures by hour of day (UTC)", 22, x, 0, w, 34);
            ChartWithAxis(hourly, UIChart.Kind.Bars, x, 40, w, 250, peak, "00", "23", $"busiest {peak:00}:00 UTC");
            Label(content, $"Busiest hour: <b>{peak:00}:00-{(peak + 1) % 24:00}:00 UTC</b>", 20, x, 330, w, 40);
        }
    }

    private void BuildClusters()
    {
        IReadOnlyList<int> communities = graph.Communities;
        if (communities.Count <= 1)
        {
            Label(content, "This dataset has no clusters (no `community` field; use a nodes_3d file).", 22, 0, 0, W, 60);
            return;
        }
        long total = 0;
        foreach (GraphNode node in graph.Nodes.Values) total += node.value;

        var sb = new StringBuilder("<b>Cluster<pos=16%>Biggest airports<pos=64%>Airports<pos=80%>Traffic</b>\n");
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
            sb.Append("<mark=#").Append(hex).Append("CC> ").Append(c + 1).Append(" </mark>")
              .Append("<pos=16%>").Append(graph.CommunityName(c))
              .Append("<pos=64%>").Append(graph.CommunityMembers(c).Count)
              .Append("<pos=80%>").Append(Pct(shares[i])).Append('\n');
        }
        Label(content, "Clusters found in the flight network (Louvain communities, not geography)", 22, 0, 0, W, 34);
        Label(content, sb.ToString(), 20, 0, 40, W, 240);
        BarsWithLabels(shares, names, 0, 290, W * 0.6f, 150);

        bool regional = graph.RegionalView;
        MakeButton(content, regional ? "Show top routes" : "Show regional view", W * 0.65f, 330, W * 0.35f, 60, ToggleView, tabActiveColor);
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

    private void BuildSelected()
    {
        GraphNode node = selector != null ? selector.SelectedNode : null;
        if (node == null)
        {
            Label(content, pinned != null
                ? $"<b>{pinned.label}</b> is pinned. Select another airport to compare them."
                : "Select an airport (trigger / pinch) to see its charts here.", 22, 0, 0, W - 260, 60);
            if (pinned != null) MakeButton(content, "Unpin", W - 240, 0, 240, 50, () => { pinned = null; Show(Tab.Selected); }, tabColor);
            return;
        }

        // Pin / unpin for a side-by-side comparison.
        if (pinned == null)
        {
            MakeButton(content, "Pin to compare", W - 240, 0, 240, 50, () => { pinned = node; Show(Tab.Selected); }, tabColor);
        }
        else
        {
            MakeButton(content, "Unpin " + pinned.ShortCode, W - 240, 0, 240, 50, () => { pinned = null; Show(Tab.Selected); }, tabColor);
            if (pinned != node)
            {
                BuildComparison(pinned, node);
                return;
            }
        }

        NodeData d = node.data;
        string place = d != null && !string.IsNullOrEmpty(d.city) ? $"{d.city}, {d.country}" : (d != null ? d.country : "");
        Label(content, $"<b>{node.label}</b>  <size=80%>{place}</size>\n{Big(node.value)} flights", 24, 0, 0, W - 260, 70);

        float half = (W - 40f) / 2f;
        bool hasMonths = d != null && d.monthly != null && d.monthly.Length > 0;
        if (hasMonths)
        {
            int n = d.monthly.Length;
            var perDay = new float[n];
            bool days = HasTimeAxis() && meta.periodDays.Length == n;
            int low = 0;
            for (int i = 0; i < n; i++)
            {
                perDay[i] = days && meta.periodDays[i] > 0 ? (float)d.monthly[i] / meta.periodDays[i] : d.monthly[i];
                if (perDay[i] < perDay[low]) low = i;
            }
            string from = days ? meta.periods[0] : "first";
            string to = days ? meta.periods[n - 1] : "last";
            Label(content, days ? "Flights per day, by month" : "Flights by period", 22, 0, 80, half, 34);
            ChartWithAxis(perDay, UIChart.Kind.Line, 0, 120, half, 250, low, from, to, $"{Num(Max(perDay))} / day");
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
            float x = hasMonths ? half + 40f : 0f;
            Label(content, "Flights by hour of day (UTC)", 22, x, 80, half, 34);
            ChartWithAxis(hours, UIChart.Kind.Bars, x, 120, half, 250, peak, "00", "23", $"busiest {peak:00}:00");
        }
        if (!hasMonths && !hasHours)
        {
            Label(content, "This dataset has no monthly or hourly counts for airports.", 22, 0, 80, W, 60);
        }
        if (d != null && d.segments != null && d.segments.Count > 0)
        {
            var list = new List<KeyValuePair<string, int>>(d.segments);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            long sum = 0;
            foreach (KeyValuePair<string, int> kv in list) sum += kv.Value;
            var sb = new StringBuilder("Segments: ");
            for (int i = 0; i < list.Count && i < 4; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(list[i].Key).Append(' ').Append(Pct((float)list[i].Value / sum));
            }
            Label(content, sb.ToString(), 20, 0, 420, W, 60);
        }
    }

    /// <summary>
    /// Two airports side by side: traffic as a share of each one's first month (so a small
    /// and a big airport compare on the same scale, e.g. recovery after COVID), plus a table.
    /// </summary>
    private void BuildComparison(GraphNode a, GraphNode b)
    {
        string colorA = ColorUtility.ToHtmlStringRGB(accentColor);
        string colorB = ColorUtility.ToHtmlStringRGB(highlightColor);
        Label(content, $"<color=#{colorA}><b>{a.ShortCode}</b></color> {Trim(ShortName(a), 28)}  vs  " +
                       $"<color=#{colorB}><b>{b.ShortCode}</b></color> {Trim(ShortName(b), 28)}", 24, 0, 0, W - 260, 60);

        float[] seriesA = PerDaySeries(a);
        float[] seriesB = PerDaySeries(b);
        bool days = HasTimeAxis();
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
            string from = days ? meta.periods[0] : "first";
            string to = days ? meta.periods[n - 1] : "last";
            Label(content, $"Traffic as % of {from} (100% = that month)", 22, 0, 60, W * 0.62f, 34);
            UIChart chart = ChartWithAxis(indexA, UIChart.Kind.Line, 0, 100, W * 0.62f, 300, -1, from, to,
                                          $"max {Max(indexA).ToString("0", Inv)}% / {Max(indexB).ToString("0", Inv)}%");
            chart.SetSecondSeries(indexB, highlightColor);

            var sb = new StringBuilder("<b><pos=0%>                 <pos=45%>" + a.ShortCode + "<pos=75%>" + b.ShortCode + "</b>\n");
            sb.Append("Flights<pos=45%>").Append(Big(a.value)).Append("<pos=75%>").Append(Big(b.value)).Append('\n');
            sb.Append("Lowest month<pos=45%>").Append(LowestLabel(indexA)).Append("<pos=75%>").Append(LowestLabel(indexB)).Append('\n');
            sb.Append("Back to 90%<pos=45%>").Append(RecoveryLabel(indexA)).Append("<pos=75%>").Append(RecoveryLabel(indexB)).Append('\n');
            sb.Append("Latest<pos=45%>").Append(indexA[n - 1].ToString("0", Inv)).Append("%<pos=75%>").Append(indexB[n - 1].ToString("0", Inv)).Append("%\n");
            Label(content, sb.ToString(), 20, W * 0.62f + 30f, 100, W * 0.38f - 30f, 300);
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append($"{a.ShortCode}: {Big(a.value)} flights\n{b.ShortCode}: {Big(b.value)} flights\n");
            sb.Append("(no monthly data in this dataset for a traffic-over-time comparison)");
            Label(content, sb.ToString(), 22, 0, 70, W, 150);
        }
    }

    /// <summary>Flights per day for each period (flights per period without a time axis), or null.</summary>
    private float[] PerDaySeries(GraphNode node)
    {
        NodeData d = node.data;
        if (d == null || d.monthly == null || d.monthly.Length == 0) return null;
        int n = d.monthly.Length;
        bool days = HasTimeAxis() && meta.periodDays.Length == n;
        var series = new float[n];
        for (int i = 0; i < n; i++)
        {
            series[i] = days && meta.periodDays[i] > 0 ? (float)d.monthly[i] / meta.periodDays[i] : d.monthly[i];
        }
        return series;
    }

    private string LowestLabel(float[] index)
    {
        int low = 0;
        for (int i = 1; i < index.Length; i++) if (index[i] < index[low]) low = i;
        return $"{index[low].ToString("0", Inv)}% ({PeriodName(low)})";
    }

    /// <summary>First period after the lowest one where traffic is back to 90% of the first period.</summary>
    private string RecoveryLabel(float[] index)
    {
        int low = 0;
        for (int i = 1; i < index.Length; i++) if (index[i] < index[low]) low = i;
        for (int i = low; i < index.Length; i++)
        {
            if (index[i] >= 90f) return PeriodName(i);
        }
        return "not yet";
    }

    private string PeriodName(int i)
    {
        return HasTimeAxis() && i < meta.periods.Length ? meta.periods[i] : "#" + (i + 1);
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

        // Sort buttons.
        string[] sortNames = { "Name", "Country", "Traffic" };
        for (int i = 0; i < sortNames.Length; i++)
        {
            FindSort sort = (FindSort)i;
            MakeButton(content, "By " + sortNames[i].ToLowerInvariant(), i * 170f, 0, 160, 44,
                       () => { findSort = sort; findPage = 0; Show(Tab.Find); }, sort == findSort ? tabActiveColor : tabColor);
        }

        // Jump buttons: first letters of names or countries (traffic has no index).
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
            float letterWidth = Mathf.Min(40f, (W - 4f * (letters.Count - 1)) / Mathf.Max(1, letters.Count));
            for (int i = 0; i < letters.Count; i++)
            {
                int page = firstIndex[letters[i]] / FindRowsPerPage;
                Button button = MakeButton(content, letters[i].ToString(), i * (letterWidth + 4f), 52, letterWidth, 40,
                                           () => { findPage = page; Show(Tab.Find); }, tabColor);
                TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
                text.fontSize = 18f;
            }
        }

        int pages = Mathf.Max(1, Mathf.CeilToInt(nodes.Count / (float)FindRowsPerPage));
        findPage = Mathf.Clamp(findPage, 0, pages - 1);
        for (int row = 0; row < FindRowsPerPage; row++)
        {
            int i = findPage * FindRowsPerPage + row;
            if (i >= nodes.Count) break;
            GraphNode node = nodes[i];
            string place = findSort == FindSort.Country ? CountryName(node) : Place(node);
            string text = $"<b>{node.ShortCode}</b>   {Trim(ShortName(node), 42)}   <size=80%><color=#A0A8B8>{place} · {Big(node.value)}</color></size>";
            bool selected = selector != null && selector.SelectedNode == node;
            MakeButton(content, text, 0, 100 + row * 40f, W, 36, () => FocusAirport(node), selected ? tabActiveColor : tabColor,
                       TextAlignmentOptions.Left);
        }

        float pagerY = 100 + FindRowsPerPage * 40f + 6f;
        MakeButton(content, "< Prev", 0, pagerY, 150, 40, () => { findPage--; Show(Tab.Find); }, tabColor);
        Label(content, $"Page {findPage + 1} / {pages}  ({nodes.Count} airports)", 20, 170, pagerY + 8, W - 340, 34,
              TextAlignmentOptions.Top);
        MakeButton(content, "Next >", W - 150, pagerY, 150, 40, () => { findPage++; Show(Tab.Find); }, tabColor);
    }

    /// <summary>Selects the airport and, if enabled, turns the XR Origin to face it.</summary>
    private void FocusAirport(GraphNode node)
    {
        if (selector != null) selector.SelectNode(node);
        if (!turnToSelection) return;

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = Camera.main;
        if (origin == null || cam == null) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        Vector3 toNode = Vector3.ProjectOnPlane(node.transform.position - cam.transform.position, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f || toNode.sqrMagnitude < 1e-6f) return;
        float angle = Vector3.SignedAngle(forward, toNode, Vector3.up);
        origin.RotateAroundCameraUsingOriginUp(angle);
        // Keep the dashboard in front after the turn.
        PlaceInFront(cam.transform);
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
            Label(content, "This dataset has no market segments or countries to filter by.", 22, 0, 0, W, 60);
            return;
        }

        float y = 0f;
        if (segmentTotals.Count > 0)
        {
            var segments = SortedKeys(segmentTotals, 7);
            Label(content, "<b>Market segment</b>", 22, 0, y, W, 32);
            var labels = new List<string> { "All" };
            foreach (string seg in segments) labels.Add(Abbreviate(seg));
            ButtonRow(labels, y + 34, i =>
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
            Label(content, "<b>Country</b> (the biggest by traffic)", 22, 0, y, W, 32);
            var labels = new List<string> { "All" };
            foreach (string country in countries) labels.Add(Trim(country, 14));
            ButtonRow(labels.GetRange(0, Mathf.Min(6, labels.Count)), y + 34, i =>
            {
                string country = i == 0 ? null : countries[i - 1];
                return (i == 0 ? graph.FilterCountry == null : graph.FilterCountry == country,
                        () => ApplyFilter(graph.FilterSegment, country, graph.FilterMinWeight));
            });
            if (labels.Count > 6)
            {
                ButtonRow(labels.GetRange(6, labels.Count - 6), y + 84, i =>
                {
                    string country = countries[i + 5];
                    return (graph.FilterCountry == country, () => ApplyFilter(graph.FilterSegment, country, graph.FilterMinWeight));
                });
            }
            y += 150f;
        }

        Label(content, "<b>Minimum flights per route</b>", 22, 0, y, W, 32);
        int[] thresholds = MinWeightPresets();
        var thresholdLabels = new List<string>();
        foreach (int t in thresholds) thresholdLabels.Add(t == 0 ? "All" : MinWeightLabel(t));
        ButtonRow(thresholdLabels, y + 34, i =>
        {
            int t = thresholds[i];
            return (graph.FilterMinWeight == t, () => ApplyFilter(graph.FilterSegment, graph.FilterCountry, t));
        });
        y += 100f;

        int visibleEdges = 0;
        foreach (GraphEdge e in graph.Edges) if (e.visible) visibleEdges++;
        int focusNodes = 0;
        foreach (GraphNode node in graph.Nodes.Values) if (graph.NodeInFocus(node)) focusNodes++;
        Label(content, $"Showing <b>{visibleEdges}</b> routes, <b>{focusNodes}</b> airports in focus", 22, 0, y, W - 260, 40);
        MakeButton(content, "Reset filters", W - 240, y - 6, 240, 46, () => ApplyFilter(null, null, 0), tabActiveColor);
    }

    private void ApplyFilter(string segment, string country, int minWeight)
    {
        // Highlights are drawn on top of the old colours: drop them before restyling.
        if (selector != null) selector.ClearSelection();
        graph.SetFilter(segment, country, minWeight);
        Show(Tab.Filters);
    }

    /// <summary>A row of equal buttons; setup(i) returns whether button i is active and its action.</summary>
    private void ButtonRow(List<string> labels, float y, System.Func<int, (bool active, UnityAction action)> setup)
    {
        const float gap = 8f;
        float width = (W - gap * (labels.Count - 1)) / labels.Count;
        for (int i = 0; i < labels.Count; i++)
        {
            (bool active, UnityAction action) = setup(i);
            Button button = MakeButton(content, labels[i], i * (width + gap), y, width, 44, action, active ? tabActiveColor : tabColor);
            TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
            text.enableAutoSizing = true;
            text.fontSizeMin = 14f;
            text.fontSizeMax = 20f;
        }
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

    private bool HasTimeAxis()
    {
        return meta != null && meta.periods != null && meta.periodDays != null && meta.periodFlights != null &&
               meta.periods.Length > 0 && meta.periodDays.Length == meta.periods.Length &&
               meta.periodFlights.Length == meta.periods.Length;
    }

    // ---- Building blocks -------------------------------------------------

    private UIChart ChartWithAxis(float[] values, UIChart.Kind kind, float x, float y, float w, float h, int highlight,
                                  string leftLabel, string rightLabel, string maxLabel)
    {
        var chartRect = new GameObject("Chart", typeof(RectTransform)).GetComponent<RectTransform>();
        chartRect.SetParent(content, false);
        Place(chartRect, x, y + 28, w, h - 56);
        UIChart chart = chartRect.gameObject.AddComponent<UIChart>();
        chart.color = accentColor;
        chart.highlightColor = highlightColor;
        chart.raycastTarget = false;
        chart.SetData(values, kind, highlight);

        Label(content, $"<size=80%>{maxLabel}</size>", 18, x, y, w, 26);
        Label(content, $"<size=80%>{leftLabel}</size>", 18, x, y + h - 26, w / 2, 26);
        Label(content, $"<size=80%>{rightLabel}</size>", 18, x + w / 2, y + h - 26, w / 2, 26, TextAlignmentOptions.TopRight);
        return chart;
    }

    private void BarsWithLabels(float[] values, string[] labels, float x, float y, float w, float h)
    {
        var chartRect = new GameObject("Bars", typeof(RectTransform)).GetComponent<RectTransform>();
        chartRect.SetParent(content, false);
        Place(chartRect, x, y, w, h - 30);
        UIChart chart = chartRect.gameObject.AddComponent<UIChart>();
        chart.color = accentColor;
        chart.raycastTarget = false;
        chart.SetData(values, UIChart.Kind.Bars, 0);
        chart.highlightColor = highlightColor;

        float slot = w / Mathf.Max(1, values.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            Label(content, $"<size=75%>{labels[i]}</size>", 16, x + i * slot, y + h - 28, slot, 28, TextAlignmentOptions.Top);
        }
    }

    private TextMeshProUGUI Label(RectTransform parent, string text, float fontSize, float x, float y, float w, float h,
                                  TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = fontSize;
        t.alignment = align;
        t.color = Color.white;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.raycastTarget = false;
        Place(t.rectTransform, x, y, w, h);
        return t;
    }

    private Button MakeButton(RectTransform parent, string label, float x, float y, float w, float h, UnityAction onClick, Color color,
                              TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        Image image = NewImage("Button " + label, parent, color);
        Place(image.rectTransform, x, y, w, h);
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => PlaySound(clickSound));
        button.onClick.AddListener(onClick);

        // Hover blip when an XR ray / finger / mouse enters the button.
        EventTrigger trigger = image.gameObject.AddComponent<EventTrigger>();
        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(_ => PlaySound(hoverSound));
        trigger.triggers.Add(enter);
        TextMeshProUGUI text = Label(image.rectTransform, label, 22, 0, 0, w, h, align);
        Stretch(text.rectTransform);
        if (align != TextAlignmentOptions.Center) text.margin = new Vector4(14f, 0f, 14f, 0f);
        return button;
    }

    private static Image NewImage(string name, RectTransform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    /// <summary>Top-left anchored rect at (x, y) from the parent's top-left, y growing downwards.</summary>
    private static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
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

    /// <summary>Short axis labels for market segments.</summary>
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
