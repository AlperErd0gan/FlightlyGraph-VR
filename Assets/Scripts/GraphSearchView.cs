using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Graph search, shown step by step (GraphSearch). While an airport is selected a small bar
/// hangs under the info card: BFS (layer by layer), DFS (one branch at a time, going back up
/// when a branch ends), 1 / 2 transfers (airports reachable with at most one / two changes of
/// plane). The airports light up
/// in the order the search finds them, coloured by their layer / depth, each joined to the
/// airport it was found from; Pause, Slower, Faster and Stop control it. The bar hides while
/// a route between two airports is shown. Only the routes the graph shows count (month,
/// filters, city groups).
/// Overlay only: it never changes the airports' size or colour (the selector, timeline and
/// RouteDraw do). Self-contained: it adds itself when the scene has a GraphSelector and
/// changes no other script.
/// </summary>
public class GraphSearchView : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Airports per second while a search plays (Slower / Faster change it).")]
    public float stepsPerSecond = 6f;
    [Tooltip("Pause (s) before each new BFS layer.")]
    public float layerPause = 0.6f;
    [Tooltip("Marker size relative to the airport.")]
    public float markerScale = 1.8f;
    [Tooltip("Width (m) of the lines from an airport to the one it was found from.")]
    public float lineWidth = 0.006f;
    [Tooltip("Colours by layer / depth: start, 1 stop, 2 stops, ... (the last one repeats).")]
    public Color[] layerColors =
    {
        Color.white,
        new Color(0.30f, 0.75f, 1f, 1f),
        new Color(0.45f, 0.85f, 0.55f, 1f),
        new Color(1f, 0.80f, 0.30f, 1f),
        new Color(1f, 0.50f, 0.30f, 1f),
        new Color(0.85f, 0.45f, 1f, 1f),
    };

    private enum Mode { Hidden, Idle, Running }
    private Mode mode = Mode.Hidden;

    private const float BarWidth = 480f;   // canvas units, like the info card
    private const float BarHeight = 104f;

    // Bar.
    private Canvas bar;
    private RectTransform idleRow;
    private RectTransform runRow;
    private TextMeshProUGUI title;
    private Button pauseButton;
    private Transform infoCard;
    private float nextInfoLookup;
    private StartPanel startPanel;

    // Search being shown.
    private Coroutine running;
    private bool paused;
    private float speed = 1f;
    private Transform overlay;
    private Transform cursor;
    private readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();
    private Material lineMaterial;

    private bool routesDimmed; // the selected airport's yellow routes are dimmed while a search shows

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<GraphSearchView>() != null) return;
        GraphSelector s = FindFirstObjectByType<GraphSelector>();
        if (s != null) s.gameObject.AddComponent<GraphSearchView>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (graph == null || selector == null)
        {
            enabled = false;
            return;
        }
        selector.NodeSelected += OnNodeSelected;
        selector.EdgeSelected += OnEdgeSelected;
        selector.SelectionCleared += OnCleared;
        selector.PathSelected += OnPath;
        graph.LayoutChanged += Stop;
        graph.PeriodChanged += Stop;
        graph.FilterChanged += Stop;
        UIKit.EnsureXREventSystem();
        BuildBar();
    }

    private void OnDestroy()
    {
        if (selector != null)
        {
            selector.NodeSelected -= OnNodeSelected;
            selector.EdgeSelected -= OnEdgeSelected;
            selector.SelectionCleared -= OnCleared;
            selector.PathSelected -= OnPath;
        }
        if (graph != null)
        {
            graph.LayoutChanged -= Stop;
            graph.PeriodChanged -= Stop;
            graph.FilterChanged -= Stop;
        }
        ClearOverlay();
        if (bar != null) Destroy(bar.gameObject);
        foreach (Material m in materials.Values) if (m != null) Destroy(m);
        if (lineMaterial != null) Destroy(lineMaterial);
    }

    // ---- Selection -----------------------------------------------------------

    private void OnNodeSelected(GraphNode node)
    {
        Stop();
        SetMode(Mode.Idle);
    }

    private void OnEdgeSelected(GraphEdge edge)
    {
        Stop();
        SetMode(Mode.Hidden);
    }

    private void OnCleared()
    {
        Stop();
        SetMode(Mode.Hidden);
    }

    /// <summary>A route between two airports is shown (ConnectionFinder / RouteDraw): no search bar meanwhile.</summary>
    private void OnPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        Stop();
        SetMode(Mode.Hidden);
    }

    // ---- Searches ------------------------------------------------------------

    private void Run(string name, int maxDepth, bool depthFirst)
    {
        GraphNode start = selector.SelectedNode;
        if (start == null || !graph.IsLoaded) return;
        Stop();
        DimSelectedRoutes(start);
        List<GraphSearch.Step> steps = depthFirst ? GraphSearch.Dfs(graph, start) : GraphSearch.Bfs(graph, start, maxDepth);
        paused = false;
        UIKit.SetButtonLabel(pauseButton, "Pause");
        SetMode(Mode.Running);
        running = StartCoroutine(Play(name, start, steps, depthFirst, maxDepth));
    }

    private IEnumerator Play(string name, GraphNode start, List<GraphSearch.Step> steps, bool depthFirst, int maxDepth)
    {
        overlay = new GameObject("GraphSearch Overlay").transform;
        cursor = MakeSphere("Cursor", Color.white).transform;
        cursor.localScale = Vector3.one * start.transform.lossyScale.x * markerScale * 1.3f;

        int total = graph.Nodes.Count;
        int visited = 0;
        int layer = 0;
        var perLayer = new List<int>();
        foreach (GraphSearch.Step step in steps)
        {
            while (paused) yield return null;
            if (!step.backtrack && !depthFirst && step.depth > layer)
            {
                layer = step.depth;
                yield return Wait(layerPause);
            }
            cursor.position = step.node.transform.position;
            if (step.backtrack)
            {
                title.text = $"{name} from {start.ShortCode} · back to {step.node.ShortCode} (depth {step.depth}) · {visited}/{total}";
                yield return Wait(0.5f / stepsPerSecond);
                continue;
            }
            visited++;
            while (perLayer.Count <= step.depth) perLayer.Add(0);
            perLayer[step.depth]++;
            ShowVisit(step);
            title.text = depthFirst
                ? $"{name} from {start.ShortCode} · depth {step.depth} · {visited}/{total} airports"
                : $"{name} from {start.ShortCode} · {Hops(step.depth)} · {visited}/{total} airports";
            // A blip per airport, unless it plays so fast that it would only buzz.
            if (step.depth > 0 && stepsPerSecond * speed <= 8f) UISounds.Play(UISounds.Hover, step.node.transform.position);
            yield return Wait(1f / stepsPerSecond);
        }

        if (cursor != null) Destroy(cursor.gameObject);
        UISounds.Play(UISounds.Click, start.transform.position);
        title.text = Summary(name, start, visited, total, perLayer, depthFirst, maxDepth);
        running = null;
        UIKit.SetButtonLabel(pauseButton, "Again");
    }

    private IEnumerator Wait(float seconds)
    {
        for (float t = 0f; t < seconds; t += Time.deltaTime * speed)
        {
            yield return null;
            while (paused) yield return null;
        }
    }

    private static string Summary(string name, GraphNode start, int visited, int total, List<int> perLayer, bool depthFirst, int maxDepth)
    {
        if (depthFirst)
        {
            return $"{name} from {start.ShortCode}: {visited}/{total} airports, deepest branch {perLayer.Count - 1} legs";
        }
        var sb = new System.Text.StringBuilder();
        if (maxDepth < int.MaxValue)
        {
            sb.Append($"With {Hops(maxDepth).Replace("direct", "direct flights only")} at most from {start.ShortCode}: {visited - 1} airports");
        }
        else
        {
            sb.Append($"{name} from {start.ShortCode}: {visited - 1} airports in {perLayer.Count - 1} layers");
        }
        sb.Append("\n<size=80%>");
        for (int d = 1; d < perLayer.Count; d++)
        {
            if (d > 1) sb.Append(" · ");
            sb.Append(Hops(d)).Append(": ").Append(perLayer[d]);
        }
        if (visited < total) sb.Append(" · not reached: ").Append(total - visited);
        sb.Append("</size>");
        return sb.ToString();
    }

    /// <summary>Layer d of a BFS in aviation terms: 1 = direct, 2 = 1 transfer, ...</summary>
    private static string Hops(int depth)
    {
        if (depth <= 0) return "start";
        if (depth == 1) return "direct";
        return (depth - 1) + (depth == 2 ? " transfer" : " transfers");
    }

    private void ShowVisit(GraphSearch.Step step)
    {
        Color c = LayerColor(step.depth);
        GameObject marker = MakeSphere("Visit " + step.node.ShortCode, new Color(c.r, c.g, c.b, 0.55f));
        marker.transform.position = step.node.transform.position;
        StartCoroutine(Grow(marker.transform, step.node.transform.lossyScale.x * markerScale));
        if (step.via != null && step.parent != null && step.via.points != null && step.via.points.Length >= 2)
        {
            LineRenderer line = MakeLine("Found " + step.node.ShortCode, c, lineWidth);
            StartCoroutine(GrowLine(line, Oriented(step.via, step.parent)));
        }
    }

    private Color LayerColor(int depth)
    {
        if (layerColors == null || layerColors.Length == 0) return Color.white;
        return layerColors[Mathf.Min(depth, layerColors.Length - 1)];
    }

    private void TogglePause()
    {
        if (running == null)
        {
            // Finished: "Again" replays the last kind of search from the selected airport.
            Again();
            return;
        }
        paused = !paused;
        UIKit.SetButtonLabel(pauseButton, paused ? "Resume" : "Pause");
    }

    private string lastName;
    private int lastDepth;
    private bool lastDepthFirst;

    private void Begin(string name, int maxDepth, bool depthFirst)
    {
        lastName = name;
        lastDepth = maxDepth;
        lastDepthFirst = depthFirst;
        Run(name, maxDepth, depthFirst);
    }

    private void Again()
    {
        if (lastName != null) Run(lastName, lastDepth, lastDepthFirst);
    }

    private void ChangeSpeed(float factor)
    {
        speed = Mathf.Clamp(speed * factor, 0.25f, 8f);
    }

    /// <summary>Stops a search and removes its marks; the bar goes back to its buttons.</summary>
    private void Stop()
    {
        if (running != null) StopCoroutine(running);
        running = null;
        paused = false;
        ClearOverlay();
        if (routesDimmed)
        {
            // Yellow routes of the selected airport back (no selection events, panels stay as they are).
            routesDimmed = false;
            if (selector != null) selector.RefreshSelection();
        }
        if (mode == Mode.Running) SetMode(selector != null && selector.SelectedNode != null ? Mode.Idle : Mode.Hidden);
    }

    /// <summary>
    /// The selected airport's routes, lit yellow by the selector, are drawn like all other routes
    /// while a search shows, so only the search's own lines stand out. Undone in Stop.
    /// </summary>
    private void DimSelectedRoutes(GraphNode start)
    {
        foreach (GraphEdge e in graph.EdgesOf(start.id))
        {
            Color c = e.baseColor;
            c.a = selector.dimmedEdgeAlpha;
            graph.SetEdgeDisplay(e, c, graph.edgeWidth);
        }
        routesDimmed = true;
    }

    private void ClearOverlay()
    {
        if (overlay != null) Destroy(overlay.gameObject);
        overlay = null;
        cursor = null;
    }

    // ---- Bar -----------------------------------------------------------------

    private void SetMode(Mode value)
    {
        mode = value;
        if (bar == null) return;
        idleRow.gameObject.SetActive(mode == Mode.Idle);
        runRow.gameObject.SetActive(mode == Mode.Running);
        if (mode == Mode.Idle && selector.SelectedNode != null)
        {
            title.text = "Search from " + selector.SelectedNode.ShortCode;
        }
    }

    private void LateUpdate()
    {
        if (bar == null) return;
        FindInfoCard();
        // Only with the info card: its hide rules (dashboard open, walked away) apply to the bar too.
        bool show = mode != Mode.Hidden && selector.SelectedNode != null && !GuidedTour.InputLocked &&
                    infoCard != null && infoCard.gameObject.activeInHierarchy &&
                    (startPanel == null || !startPanel.IsOpen);
        if (bar.gameObject.activeSelf != show) bar.gameObject.SetActive(show);
        if (!show) return;
        Place();
    }

    private void FindInfoCard()
    {
        if (infoCard != null && infoCard.gameObject.activeInHierarchy && startPanel != null) return;
        if (Time.unscaledTime < nextInfoLookup) return;
        nextInfoLookup = Time.unscaledTime + 0.5f;
        if (infoCard == null || !infoCard.gameObject.activeInHierarchy)
        {
            GameObject info = GameObject.Find("InfoPanel");
            infoCard = info != null ? info.transform : null;
        }
        if (startPanel == null) startPanel = FindFirstObjectByType<StartPanel>();
    }

    /// <summary>Just under the info card, same size and angle (the card's pivot is its bottom edge).</summary>
    private void Place()
    {
        Transform t = bar.transform;
        t.rotation = infoCard.rotation;
        t.localScale = infoCard.lossyScale;
        t.position = infoCard.position - infoCard.up * (0.012f + BarHeight * 0.5f * infoCard.lossyScale.y);
    }

    private void BuildBar()
    {
        bar = UIKit.WorldCanvas("Graph Search Bar", new Vector2(BarWidth, BarHeight));
        Transform root = bar.transform;
        Image background = UIKit.Box(root, "Background", UIKit.PanelColor, UIKit.CardRadius);
        UIKit.Stretch(background.rectTransform);
        title = UIKit.Text(root, "", UIKit.SmallSize, UIKit.TextColor, 14f, 6f, BarWidth - 28f, 42f, TextAlignmentOptions.Left);
        title.textWrappingMode = TextWrappingModes.Normal;

        const float gap = 8f;
        float w4 = (BarWidth - 28f - 3f * gap) / 4f;
        float y = 52f;
        const float h = 44f;

        idleRow = UIKit.NewRect("Idle", root);
        UIKit.Stretch(idleRow);
        UIKit.Button(idleRow, "BFS", () => Begin("BFS", int.MaxValue, false), 14f, y, w4, h, UIKit.ButtonStyle.Primary, UIKit.BodySize);
        UIKit.Button(idleRow, "DFS", () => Begin("DFS", int.MaxValue, true), 14f + (w4 + gap), y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.BodySize);
        // Depth 2 / 3: a direct flight is depth 1, one transfer (change of plane) depth 2.
        UIKit.Button(idleRow, "1 transfer", () => Begin("Reach", 2, false), 14f + 2f * (w4 + gap), y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.SmallSize);
        UIKit.Button(idleRow, "2 transfers", () => Begin("Reach", 3, false), 14f + 3f * (w4 + gap), y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.SmallSize);

        runRow = UIKit.NewRect("Running", root);
        UIKit.Stretch(runRow);
        pauseButton = UIKit.Button(runRow, "Pause", TogglePause, 14f, y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.BodySize);
        UIKit.Button(runRow, "Slower", () => ChangeSpeed(0.5f), 14f + (w4 + gap), y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.BodySize);
        UIKit.Button(runRow, "Faster", () => ChangeSpeed(2f), 14f + 2f * (w4 + gap), y, w4, h, UIKit.ButtonStyle.Secondary, UIKit.BodySize);
        UIKit.Button(runRow, "Stop", () => { Stop(); SetMode(Mode.Idle); }, 14f + 3f * (w4 + gap), y, w4, h,
                     UIKit.ButtonStyle.Secondary, UIKit.BodySize);

        SetMode(Mode.Hidden);
        bar.gameObject.SetActive(false);
    }

    // ---- Overlay -------------------------------------------------------------

    private IEnumerator Grow(Transform marker, float size)
    {
        for (float t = 0f; t < 0.25f && marker != null; t += Time.deltaTime)
        {
            float s = t / 0.25f;
            marker.localScale = Vector3.one * size * (1f - (1f - s) * (1f - s));
            yield return null;
        }
        if (marker != null) marker.localScale = Vector3.one * size;
    }

    private IEnumerator GrowLine(LineRenderer line, Vector3[] points)
    {
        for (float t = 0f; t < 0.3f && line != null; t += Time.deltaTime)
        {
            SetLine(line, points, t / 0.3f);
            yield return null;
        }
        if (line != null) SetLine(line, points, 1f);
    }

    /// <summary>Shows the first `amount` (0..1) of the arc, by point count.</summary>
    private static void SetLine(LineRenderer line, Vector3[] points, float amount)
    {
        float f = Mathf.Clamp01(amount) * (points.Length - 1);
        int whole = Mathf.FloorToInt(f);
        int count = Mathf.Min(points.Length, whole + 2);
        line.positionCount = count;
        for (int i = 0; i < count; i++)
        {
            line.SetPosition(i, i <= whole ? points[i] : Vector3.Lerp(points[whole], points[Mathf.Min(whole + 1, points.Length - 1)], f - whole));
        }
    }

    /// <summary>The edge's arc starting at `from` (edges are stored in one direction only).</summary>
    private static Vector3[] Oriented(GraphEdge edge, GraphNode from)
    {
        var points = (Vector3[])edge.points.Clone();
        Vector3 at = from.transform.position;
        if ((points[points.Length - 1] - at).sqrMagnitude < (points[0] - at).sqrMagnitude) System.Array.Reverse(points);
        return points;
    }

    private LineRenderer MakeLine(string name, Color color, float width)
    {
        if (overlay == null) overlay = new GameObject("GraphSearch Overlay").transform;
        var go = new GameObject(name);
        go.transform.SetParent(overlay, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 0;
        line.widthMultiplier = width;
        line.numCapVertices = 3;
        line.startColor = line.endColor = color;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "GraphSearch Line (runtime)" };
        line.sharedMaterial = lineMaterial;
        return line;
    }

    private GameObject MakeSphere(string name, Color color)
    {
        if (overlay == null) overlay = new GameObject("GraphSearch Overlay").transform;
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>()); // must never block XR rays or selection
        go.transform.SetParent(overlay, true);
        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        if (!materials.TryGetValue(color, out Material m) || m == null)
        {
            m = new Material(Shader.Find("Sprites/Default")) { name = "GraphSearch Marker (runtime)", color = color };
            materials[color] = m;
        }
        r.sharedMaterial = m;
        return go;
    }
}
