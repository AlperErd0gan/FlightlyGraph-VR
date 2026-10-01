using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// A guided tour for demos: started from the data dashboard's Tour button, it walks a
/// newcomer through the project in about two minutes, each step with a caption and
/// (if a NodeNarrator is present) spoken text:
///   1. what the scene shows (airports, routes, flights, period)
///   2. the biggest hubs (selects the top airport and turns you to it)
///   3. traffic over time (dashboard Overview chart: lowest month, recovery)
///   4. regional clusters (switches to the regional view)
///   5. a connecting route (no direct flight, shortest route via a hub)
///   6. how to explore on your own
/// Every sentence is built from the loaded data, so the tour works with any dataset;
/// steps whose data is missing (no time axis, no clusters) are skipped.
/// Stop any time with the Stop button on the caption.
/// </summary>
public class GuidedTour : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    public DataDashboard dashboard;
    public GraphViewMode viewMode;
    public ConnectionFinder connections;
    public NodeNarrator narrator;

    [Header("Timing")]
    [Tooltip("Pause after each step's narration (s).")]
    public float pauseBetweenSteps = 1.2f;
    [Tooltip("A step never lasts longer than this (s), even if the speaker keeps reporting speech.")]
    public float maxStepSeconds = 45f;
    [Tooltip("How long to wait for the speech to start (Wit.ai fetches it over the network) before moving on without it (s).")]
    public float speechStartTimeout = 8f;
    [Tooltip("Each step stays at least this long, and at least as long as reading its caption takes (s).")]
    public float minStepSeconds = 4f;

    [Header("Caption")]
    public float captionDistance = 1.0f;
    [Tooltip("Height of the caption relative to the eyes (m); below the gaze so the graph stays visible.")]
    public float captionHeight = -0.38f;
    [Tooltip("Caption height while the dashboard is open (m), low enough not to cover its chart.")]
    public float captionHeightWithDashboard = -0.62f;
    [Tooltip("The caption lazily follows the head: it moves once you turn this many degrees away from it...")]
    public float followAngle = 35f;
    [Tooltip("...or walk this far (m); beyond snapDistance it jumps.")]
    public float followDistance = 0.35f;
    public float snapDistance = 2f;
    public float followSpeed = 3f;
    public Color captionColor = new Color(0.06f, 0.07f, 0.1f, 0.9f);
    public Color buttonColor = new Color(0.22f, 0.45f, 0.65f, 1f);

    public bool IsRunning { get; private set; }

    /// <summary>
    /// True while a tour runs: user input (selection, routes, dashboard, view mode, hand
    /// teleport) is ignored so nobody can derail the demo; only the Stop button works.
    /// Checked by XRGraphInput, GraphSelector, ConnectionFinder, DataDashboard,
    /// GraphViewMode and HandTeleport.
    /// </summary>
    public static bool InputLocked { get; private set; }

    private delegate string StepAction();

    private Coroutine running;
    private Canvas caption;
    private TextMeshProUGUI captionText;
    private TextMeshProUGUI stepText;
    private GraphViewMode.ViewMode modeBefore;
    private bool following;
    // XRI teleport interactors (thumbstick teleport) switched off during the tour.
    private readonly List<XRBaseInteractor> pausedTeleporters = new List<XRBaseInteractor>();

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (dashboard == null) dashboard = FindFirstObjectByType<DataDashboard>();
        if (viewMode == null) viewMode = FindFirstObjectByType<GraphViewMode>();
        if (connections == null) connections = FindFirstObjectByType<ConnectionFinder>();
        if (narrator == null) narrator = FindFirstObjectByType<NodeNarrator>();
    }

    private void Start()
    {
        BuildCaption();
        caption.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        InputLocked = false;
        if (caption != null) Destroy(caption.gameObject);
    }

    private void OnDisable()
    {
        StopTour();
        InputLocked = false;
    }

    public void StartTour()
    {
        if (graph == null || !graph.IsLoaded) return;
        StopTour();
        running = StartCoroutine(Run());
    }

    public void StopTour()
    {
        if (running != null) StopCoroutine(running);
        running = null;
        if (IsRunning) Finish();
    }

    // ---- Flow ------------------------------------------------------------

    private IEnumerator Run()
    {
        IsRunning = true;
        InputLocked = true;
        PauseTeleporters();
        modeBefore = viewMode != null ? viewMode.Mode : GraphViewMode.ViewMode.TopRoutes;
        if (narrator != null) narrator.AutoNarration = false;
        if (graph.HasFilter) graph.SetFilter(null, null, 0);
        graph.SetPeriod(-1); // the tour talks about all months together
        if (viewMode != null && viewMode.Mode != GraphViewMode.ViewMode.TopRoutes) viewMode.Apply(GraphViewMode.ViewMode.TopRoutes, false);

        var steps = new List<StepAction> { Intro, Hubs, TrafficOverTime, Insights, Clusters, ConnectingRoute, Outro };
        int total = steps.Count;
        for (int i = 0; i < steps.Count; i++)
        {
            // A failing step is logged and skipped instead of stopping the tour half way
            // (the coroutine would die and leave the input locked).
            string text;
            try
            {
                text = steps[i]();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"GuidedTour: step {i + 1} ({steps[i].Method.Name}) failed: {ex}");
                text = null;
            }
            if (string.IsNullOrEmpty(text))
            {
                Debug.Log($"GuidedTour: step {i + 1} ({steps[i].Method.Name}) skipped");
                continue;
            }
            yield return Say(text, i + 1, total);
        }
        Finish();
        running = null;
    }

    private void Finish()
    {
        IsRunning = false;
        InputLocked = false;
        ResumeTeleporters();
        if (narrator != null)
        {
            narrator.Stop();
            narrator.AutoNarration = true;
        }
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
        if (viewMode != null && viewMode.Mode != modeBefore) viewMode.Apply(modeBefore, false);
        if (caption != null) caption.gameObject.SetActive(false);
    }

    /// <summary>Shows the caption, speaks it and waits until the speech is over (or an estimate of it).</summary>
    private IEnumerator Say(string text, int step, int total)
    {
        ShowCaption(text, step, total);
        if (narrator != null) narrator.SpeakText(text, caption.transform.position);

        float start = Time.time;
        // The caption must stay up at least as long as reading it takes, speech or not.
        float readTime = Mathf.Max(minStepSeconds, NodeNarrator.EstimateSeconds(text));
        bool started = false;
        float startedAt = -1f;
        while (Time.time - start < maxStepSeconds)
        {
            float elapsed = Time.time - start;
            bool? speaking = narrator != null ? narrator.IsSpeaking : null;
            if (speaking == true && !started)
            {
                started = true;
                startedAt = elapsed;
            }
            // Speech is over when it has started and stopped; if it never starts (no SDK state,
            // no network, no narrator) give up waiting for it after speechStartTimeout.
            bool speechOver = !speaking.HasValue || (started && !speaking.Value) || (!started && elapsed > speechStartTimeout);
            if (speechOver && elapsed >= readTime) break;
            yield return null;
        }
        Debug.Log($"GuidedTour: step {step}/{total} took {Time.time - start:F1} s " +
                  (started ? $"(speech started after {startedAt:F1} s)" : "(no speech detected)"));
        yield return new WaitForSeconds(pauseBetweenSteps);
    }

    // ---- Steps (each returns its sentence, or null to skip) ----------------

    private string Intro()
    {
        ClearScene();
        long flights = 0;
        foreach (GraphNode node in graph.Nodes.Values) flights += node.value;
        flights /= 2; // every flight is counted at both of its airports
        string period = "";
        MetaData axis = dashboard != null ? dashboard.TimeAxis : null;
        if (axis != null)
        {
            period = $", {Year(axis.periods[0])} to {Year(axis.periods[axis.periods.Length - 1])}";
            flights = axis.totalFlights;
        }
        return $"Welcome to FlightlyVR. Each sphere is an airport, each line a route. " +
               $"{graph.Nodes.Count} airports, {NodeNarrator.SpokenAmount(flights)} flights{period}.";
    }

    private string Hubs()
    {
        List<GraphNode> top = TopNodes(1);
        if (top.Count == 0) return null;
        GraphNode hub = top[0];
        selector.SelectNode(hub);
        TurnToward(hub.transform.position);
        return $"The biggest hub is {Name(hub)}. Bigger spheres mean busier airports.";
    }

    private string TrafficOverTime()
    {
        MetaData axis = dashboard != null ? dashboard.TimeAxis : null;
        if (axis == null || axis.periods.Length < 3) return null;
        selector.ClearSelection();
        dashboard.OpenTab(DataDashboard.Tab.Overview);

        int n = axis.periods.Length;
        var perDay = new float[n];
        int low = 0;
        for (int i = 0; i < n; i++)
        {
            perDay[i] = axis.periodDays[i] > 0 ? (float)axis.periodFlights[i] / axis.periodDays[i] : 0f;
            if (perDay[i] < perDay[low]) low = i;
        }
        if (perDay[0] <= 0f) return null;
        float lowShare = perDay[low] / perDay[0] * 100f;
        float lastShare = perDay[n - 1] / perDay[0] * 100f;
        return $"In {MonthName(axis.periods[low])}, flights fell to {lowShare:0} percent of {MonthName(axis.periods[0])}. " +
               $"By {MonthName(axis.periods[n - 1])} they were at {lastShare:0} percent.";
    }

    private string Insights()
    {
        if (dashboard == null) return null;
        List<GraphInsight> found = GraphInsights.Compute(graph, dashboard.TimeAxis, 1);
        GraphInsight fastest = found.Find(i => i.category == GraphInsights.FastestRecovery);
        GraphInsight slowest = found.Find(i => i.category == GraphInsights.SlowestRecovery);
        if (fastest == null && slowest == null) return null;
        dashboard.OpenTab(DataDashboard.Tab.Insights);
        return ((fastest != null ? fastest.spoken : "") + " " + (slowest != null ? slowest.spoken : "")).Trim();
    }

    private string Clusters()
    {
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
        IReadOnlyList<int> communities = graph.Communities;
        if (communities.Count <= 1 || viewMode == null) return null;
        selector.ClearSelection();
        viewMode.Apply(GraphViewMode.ViewMode.Regional, false);
        return $"Colours now show {communities.Count} clusters of airports that mostly fly to each other. " +
               $"The largest is around {Name(graph.CommunityMembers(communities[0])[0])}.";
    }

    private string ConnectingRoute()
    {
        if (viewMode != null) viewMode.Apply(GraphViewMode.ViewMode.TopRoutes, false);
        if (connections == null)
        {
            Debug.LogWarning("GuidedTour: no ConnectionFinder in the scene; connecting-route step skipped.");
            return null;
        }
        List<GraphNode> top = TopNodes(1);
        if (top.Count == 0) return null;
        GraphNode hub = top[0];

        // The farthest airport (most stops) from the biggest hub; among equals the busiest,
        // so the example uses an airport people may know.
        GraphNode target = null;
        List<GraphNode> bestNodes = null;
        List<GraphEdge> bestEdges = null;
        foreach (GraphNode candidate in graph.Nodes.Values)
        {
            if (candidate == hub) continue;
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            if (!connections.TryFindPath(hub, candidate, nodes, edges)) continue;
            bool better = bestEdges == null || edges.Count > bestEdges.Count ||
                          (edges.Count == bestEdges.Count && candidate.value > target.value);
            if (better)
            {
                target = candidate;
                bestNodes = nodes;
                bestEdges = edges;
            }
        }
        if (target == null || bestEdges.Count < 2)
        {
            Debug.Log($"GuidedTour: every airport is one flight from {hub.label}; connecting-route step skipped.");
            return null;
        }

        selector.SelectPath(bestNodes, bestEdges);
        Debug.Log($"GuidedTour: connecting route {string.Join(" > ", bestNodes.ConvertAll(n => n.ShortCode))}");
        TurnToward(target.transform.position);
        var via = new List<GraphNode>();
        for (int i = 1; i < bestNodes.Count - 1; i++) via.Add(bestNodes[i]);
        return $"No direct flight from {Name(hub)} to {Name(target)}. The shortest way is via {SpokenList(via, via.Count)}.";
    }

    private string Outro()
    {
        ClearScene();
        return "Your turn: point at an airport and press the trigger. The left menu button opens the dashboard.";
    }

    // ---- Helpers ---------------------------------------------------------

    private void ClearScene()
    {
        if (selector != null) selector.ClearSelection();
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
    }

    private List<GraphNode> TopNodes(int count)
    {
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        nodes.Sort((a, b) => a.value != b.value ? b.value.CompareTo(a.value) : string.CompareOrdinal(a.id, b.id));
        if (nodes.Count > count) nodes.RemoveRange(count, nodes.Count - count);
        return nodes;
    }

    private static string Name(GraphNode node) => NodeNarrator.SpokenShortName(node);

    private static string SpokenList(IReadOnlyList<GraphNode> nodes, int max)
    {
        var names = new List<string>();
        for (int i = 0; i < nodes.Count && i < max; i++) names.Add(Name(nodes[i]));
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
    }

    private static readonly string[] Months =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    /// <summary>"2020-04" -> "April 2020".</summary>
    private static string MonthName(string period)
    {
        if (period != null && period.Length >= 7 && int.TryParse(period.Substring(5, 2), out int month) && month >= 1 && month <= 12)
        {
            return Months[month - 1] + " " + period.Substring(0, 4);
        }
        return period;
    }

    private static string Year(string period) => period != null && period.Length >= 4 ? period.Substring(0, 4) : period;

    /// <summary>Turns the XR Origin (snap, around the head) so the point is straight ahead.</summary>
    private void TurnToward(Vector3 point)
    {
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = Camera.main;
        if (origin == null || cam == null) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        Vector3 toPoint = Vector3.ProjectOnPlane(point - cam.transform.position, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f || toPoint.sqrMagnitude < 1e-6f) return;
        origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(forward, toPoint, Vector3.up));
    }

    /// <summary>
    /// Switches off the XRI interactors that teleport (those on the "Teleport" interaction
    /// layer, e.g. the thumbstick teleport ray). Near-Far and poke interactors stay on: they
    /// press the caption's Stop button.
    /// </summary>
    private void PauseTeleporters()
    {
        pausedTeleporters.Clear();
        int teleportLayer = InteractionLayerMask.GetMask("Teleport");
        if (teleportLayer == 0) return;
        foreach (XRBaseInteractor interactor in FindObjectsByType<XRBaseInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (interactor is NearFarInteractor || interactor is XRPokeInteractor) continue;
            if (!interactor.enabled || (interactor.interactionLayers.value & teleportLayer) == 0) continue;
            interactor.enabled = false;
            pausedTeleporters.Add(interactor);
        }
    }

    private void ResumeTeleporters()
    {
        foreach (XRBaseInteractor interactor in pausedTeleporters)
        {
            if (interactor != null) interactor.enabled = true;
        }
        pausedTeleporters.Clear();
    }

    /// <summary>Lazy follow, like the dashboard: stays while you read, glides back in front when you turn or walk.</summary>
    private void LateUpdate()
    {
        if (!IsRunning || caption == null || !caption.gameObject.activeSelf) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Vector3 forward = FlatForward(head);
        Vector3 target = CaptionTarget(head, forward);
        Transform panel = caption.transform;
        float offset = Vector3.Distance(panel.position, target);
        if (offset > snapDistance)
        {
            panel.position = target;
            panel.rotation = Quaternion.LookRotation(forward, Vector3.up);
            following = false;
            return;
        }
        Vector3 toPanel = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
        float angle = toPanel.sqrMagnitude > 1e-6f ? Vector3.Angle(forward, toPanel) : 0f;
        if (!following && (offset > followDistance || angle > followAngle)) following = true;
        if (!following) return;
        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        panel.position = Vector3.Lerp(panel.position, target, t);
        Vector3 away = Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up);
        if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.Slerp(panel.rotation, Quaternion.LookRotation(away, Vector3.up), t);
        if (Vector3.Distance(panel.position, target) < 0.03f) following = false;
    }

    private static Vector3 FlatForward(Transform head)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        return forward.normalized;
    }

    private Vector3 CaptionTarget(Transform head, Vector3 forward)
    {
        float height = dashboard != null && dashboard.IsOpen ? captionHeightWithDashboard : captionHeight;
        return head.position + forward * captionDistance + Vector3.up * height;
    }

    // ---- Caption ---------------------------------------------------------

    private void BuildCaption()
    {
        var root = new GameObject("GuidedTour Caption", typeof(RectTransform));
        caption = root.AddComponent<Canvas>();
        caption.renderMode = RenderMode.WorldSpace;
        root.AddComponent<TrackedDeviceGraphicRaycaster>();
        root.AddComponent<GraphicRaycaster>();
        var rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = new Vector2(900f, 230f);
        rootRect.localScale = Vector3.one * 0.001f; // 1000 units = 1 m

        Image background = NewRect<Image>("Background", rootRect, 0, 0, 900, 230);
        background.color = captionColor;

        stepText = NewRect<TextMeshProUGUI>("Step", rootRect, 24, 14, 400, 30);
        stepText.fontSize = 20;
        stepText.color = new Color(1f, 1f, 1f, 0.6f);
        stepText.raycastTarget = false;

        captionText = NewRect<TextMeshProUGUI>("Text", rootRect, 24, 46, 852, 170);
        captionText.fontSize = 24;
        captionText.color = Color.white;
        captionText.textWrappingMode = TextWrappingModes.Normal;
        captionText.raycastTarget = false;

        Image stopImage = NewRect<Image>("Stop", rootRect, 900 - 24 - 150, 10, 150, 40);
        stopImage.color = buttonColor;
        Button stop = stopImage.gameObject.AddComponent<Button>();
        stop.targetGraphic = stopImage;
        stop.onClick.AddListener(StopTour);
        TextMeshProUGUI stopText = NewRect<TextMeshProUGUI>("Label", stopImage.rectTransform, 0, 0, 150, 40);
        stopText.text = "Stop tour";
        stopText.fontSize = 20;
        stopText.alignment = TextAlignmentOptions.Center;
        stopText.raycastTarget = false;
    }

    private void ShowCaption(string text, int step, int total)
    {
        stepText.text = $"Guided tour  {step} / {total}";
        captionText.text = text;
        Camera cam = Camera.main;
        if (cam != null)
        {
            // Placed at every step, after any turn, so it is always in front.
            Transform head = cam.transform;
            Vector3 forward = FlatForward(head);
            caption.transform.position = CaptionTarget(head, forward);
            caption.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            following = false;
            caption.worldCamera = cam;
        }
        caption.gameObject.SetActive(true);
    }

    /// <summary>Child UI element of type T, placed from the parent's top-left corner (y down).</summary>
    private static T NewRect<T>(string name, RectTransform parent, float x, float y, float w, float h) where T : Component
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return go.AddComponent<T>();
    }
}
