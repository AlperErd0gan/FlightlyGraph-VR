using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space info card for the current selection (airport, route or connection),
/// in the data dashboard's look (UIKit): a type tag, title and place, three key
/// figures, details, the busiest routes / the legs of a connection, a hint, and a
/// close button. Placement:
/// - InView (default): in front of the viewer, below and to the right of the gaze
///   (the middle of the view stays free for the graph); follows
///   the head lazily (moves once you turn or walk away from it, snaps after a
///   teleport), with a thin leader line to the selected node / edge;
/// - NearSelection: above the selected node (or the middle of the edge's arc),
///   pulled towards the viewer.
/// Only the close button takes XR rays / clicks, so nodes behind the card stay
/// selectable. With the timeline on one month the figures follow that month.
/// Built at runtime; not parented to the graph, so its size in metres does not
/// depend on the graph's scale.
/// </summary>
public class GraphInfoPanel : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Camera the panel faces. Defaults to Camera.main (the XR head camera).")]
    public Transform viewer;

    [Header("Text")]
    [Tooltip("Unit of node.value in the loaded data (EUROCONTROL: flights).")]
    public string valueUnit = "Flights";
    [Tooltip("Unit of edge.weight in the loaded data (EUROCONTROL: flights).")]
    public string weightUnit = "Flights";
    [Tooltip("Busiest routes listed for a selected airport.")]
    public int topNeighbours = 3;
    [Tooltip("One-line tips at the bottom of the card (how to get a route, ...).")]
    public bool showHints = true;

    public enum Placement { InView, NearSelection }

    [Header("Placement")]
    public Placement placement = Placement.InView;
    [Tooltip("InView: distance in front of the eyes (m).")]
    public float viewDistance = 0.8f;
    [Tooltip("InView: how far below the gaze line the panel's bottom edge sits (m).")]
    public float viewDownOffset = 0.25f;
    [Tooltip("InView: sideways offset of the card's centre from the gaze line (m); positive = right, negative = left, 0 = centred.")]
    public float viewSideOffset = 0.2f;
    [Tooltip("InView: the panel starts following once it is this many degrees away from where you look.")]
    public float followAngle = 30f;
    [Tooltip("InView: the panel also starts following once its target spot is this far away (walking) (m).")]
    public float followDistance = 0.25f;
    [Tooltip("InView: beyond this (e.g. after a teleport) the panel jumps instead of gliding (m).")]
    public float snapDistance = 1.5f;
    [Tooltip("InView: follow smoothing; higher = snappier.")]
    public float followSpeed = 4f;
    [Tooltip("Walking / teleporting farther than this from where you made the selection closes the panel and clears " +
             "the selection (m). How far the selected node itself is does not matter. 0 = never.")]
    public float autoClearDistance = 5f;
    [Tooltip("Thin line from the panel to the selected node / edge.")]
    public bool showLeaderLine = true;
    public Color leaderLineColor = new Color(1f, 1f, 1f, 0.5f);
    public float leaderLineWidth = 0.002f;
    [Tooltip("Optional material for the leader line (vertex colours, e.g. Sprites/Default). If unset, Sprites/Default is created.")]
    public Material leaderLineMaterial;

    [Header("Look")]
    [Tooltip("Card width in metres (the card is laid out 480 canvas units wide and scaled to this).")]
    public float cardWidth = 0.34f;
    [Tooltip("NearSelection: gap between the selection and the bottom of the panel.")]
    public float verticalOffset = 0.04f;
    [Tooltip("NearSelection: pulls the panel from the selection towards the viewer, so it floats in front of the graph instead of among the edges.")]
    public float towardViewer = 0.3f;
    [Tooltip("NearSelection: the panel never comes closer to the eyes than this.")]
    public float minViewerDistance = 0.45f;
    [Tooltip("Background opacity: lower lets the graph show through.")]
    [Range(0f, 1f)] public float backgroundAlpha = 0.88f;

    private const float Width = 480f;
    private const float Pad = 22f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Canvas canvas;
    private RectTransform root;
    private RectTransform content;
    private Image background;
    private LineRenderer leaderLine;
    // Created here at runtime, so destroyed here too.
    private readonly List<Object> ownedAssets = new List<Object>();
    private bool following;
    // While suppressed (e.g. the data dashboard is open) the panel stays hidden but keeps
    // the selection, so it comes back when the suppression ends.
    private bool suppressed;
    // Viewer position when the current selection was made (for autoClearDistance).
    private Vector3 selectionViewerPosition;
    private bool hasSelectionPosition;
    // What is shown ("node:EDDF"), how to draw it and where its leader line points.
    private string currentKey;
    private System.Action<float> build;
    private System.Func<Vector3> anchor;
    private float y; // layout cursor while building

    public bool IsShowing => canvas != null && canvas.gameObject.activeSelf;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        BuildPanel();
    }

    private void OnEnable()
    {
        if (graph != null) graph.PeriodChanged += OnPeriodChanged;
        if (selector == null) return;
        selector.NodeSelected += ShowNode;
        selector.EdgeSelected += ShowEdge;
        selector.PathSelected += ShowPath;
        selector.SelectionCleared += Hide;
    }

    private void OnDestroy()
    {
        foreach (Object asset in ownedAssets)
        {
            if (asset != null) Destroy(asset);
        }
        ownedAssets.Clear();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    private void OnDisable()
    {
        if (graph != null) graph.PeriodChanged -= OnPeriodChanged;
        if (selector == null) return;
        selector.NodeSelected -= ShowNode;
        selector.EdgeSelected -= ShowEdge;
        selector.PathSelected -= ShowPath;
        selector.SelectionCleared -= Hide;
    }

    private void Start()
    {
        UIKit.EnsureXREventSystem();
    }

    private void LateUpdate()
    {
        if (!IsShowing || !ResolveViewer()) return;
        Transform panel = canvas.transform;

        // Walked / teleported away since selecting: drop it (Hide runs via SelectionCleared). Measured from
        // where the viewer stood, not from the node, so selecting a far airport keeps working. The guided
        // tour controls the selection itself.
        if (autoClearDistance > 0f && hasSelectionPosition && !GuidedTour.InputLocked &&
            Vector3.Distance(viewer.position, selectionViewerPosition) > autoClearDistance)
        {
            selector.ClearSelection();
            return;
        }

        Vector3 target = anchor != null ? anchor() : panel.position;
        if (placement == Placement.InView) FollowView();
        else panel.position = NearSelectionPosition(target);

        // A world-space canvas reads correctly when its +Z points away from the viewer.
        Vector3 away = panel.position - viewer.position;
        if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.LookRotation(away, Vector3.up);

        leaderLine.enabled = showLeaderLine;
        if (showLeaderLine)
        {
            leaderLine.SetPosition(0, panel.position);
            leaderLine.SetPosition(1, target);
        }
    }

    private bool ResolveViewer()
    {
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
        return viewer != null;
    }

    private Vector3 ViewTarget()
    {
        Vector3 right = Vector3.ProjectOnPlane(viewer.right, Vector3.up).normalized;
        return viewer.position + viewer.forward * viewDistance + right * viewSideOffset - Vector3.up * viewDownOffset;
    }

    /// <summary>
    /// Lazy follow: stays put while you read it (small head motion does not move it),
    /// glides back in front once the head turns more than followAngle away or you
    /// walk more than followDistance, and jumps after a teleport.
    /// </summary>
    private void FollowView()
    {
        Transform panel = canvas.transform;
        Vector3 target = ViewTarget();
        float offset = Vector3.Distance(panel.position, target);
        if (offset > snapDistance)
        {
            panel.position = target;
            following = false;
            return;
        }
        if (!following &&
            (offset > followDistance ||
             Vector3.Angle(viewer.forward, panel.position - viewer.position) > followAngle))
        {
            following = true;
        }
        if (following)
        {
            panel.position = Vector3.Lerp(panel.position, target, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            if ((panel.position - target).sqrMagnitude < 0.02f * 0.02f) following = false;
        }
    }

    private Vector3 NearSelectionPosition(Vector3 target)
    {
        Vector3 position = target + Vector3.up * verticalOffset;
        Vector3 toViewer = viewer.position - position;
        float distance = toViewer.magnitude;
        if (distance > 1e-4f)
        {
            float pull = Mathf.Clamp(distance - minViewerDistance, 0f, towardViewer);
            position += toViewer / distance * pull;
        }
        return position;
    }

    // ---- Panel -----------------------------------------------------------

    private void BuildPanel()
    {
        canvas = UIKit.WorldCanvas("InfoPanel", new Vector2(Width, 200f));
        root = (RectTransform)canvas.transform;
        // Pivot at the bottom centre: the card grows upwards from its position.
        root.pivot = new Vector2(0.5f, 0f);
        root.localScale = Vector3.one * (cardWidth / Width);

        Color back = UIKit.PanelColor;
        back.a = backgroundAlpha;
        background = UIKit.Box(root, "Background", back, UIKit.PanelRadius * 0.8f);
        UIKit.Stretch(background.rectTransform);
        content = UIKit.NewRect("Content", root);
        UIKit.Stretch(content);

        leaderLine = canvas.gameObject.AddComponent<LineRenderer>();
        leaderLine.useWorldSpace = true;
        leaderLine.positionCount = 2;
        leaderLine.startWidth = leaderLineWidth;
        leaderLine.endWidth = leaderLineWidth;
        leaderLine.startColor = leaderLineColor;
        leaderLine.endColor = leaderLineColor;
        // Not the edge material: edges use a ribbon shader that needs the edge mesh's vertex layout.
        Material lineMat = leaderLineMaterial;
        if (lineMat == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                lineMat = new Material(shader);
                ownedAssets.Add(lineMat);
            }
        }
        leaderLine.sharedMaterial = lineMat;
        leaderLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        leaderLine.receiveShadows = false;

        canvas.gameObject.SetActive(false);
    }

    private void OnPeriodChanged()
    {
        if (currentKey != null && !suppressed) Render();
    }

    /// <summary>Hide the panel while another view shows the same information (the data dashboard).</summary>
    public void SetSuppressed(bool value)
    {
        suppressed = value;
        if (suppressed) canvas.gameObject.SetActive(false);
        else if (currentKey != null) Render();
    }

    private void Hide()
    {
        currentKey = null;
        build = null;
        anchor = null;
        hasSelectionPosition = false;
        canvas.gameObject.SetActive(false);
    }

    /// <summary>Shows a selection: a new one (other key) restarts the walk-away check.</summary>
    private void Present(string key, System.Action<float> builder, System.Func<Vector3> anchorPoint)
    {
        if (key != currentKey && ResolveViewer())
        {
            selectionViewerPosition = viewer.position;
            hasSelectionPosition = true;
        }
        currentKey = key;
        build = builder;
        anchor = anchorPoint;
        if (!suppressed) Render();
    }

    private void Render()
    {
        bool wasHidden = !canvas.gameObject.activeSelf;
        // Active before building: TextMeshPro gets its font in Awake, which only runs on an
        // active object, and measuring the text needs it.
        canvas.gameObject.SetActive(true);
        for (int i = content.childCount - 1; i >= 0; i--) DestroyImmediate(content.GetChild(i).gameObject);
        y = Pad;
        build(Width - 2f * Pad);
        float height = y + Pad - 6f;
        root.sizeDelta = new Vector2(Width, height);

        if (Camera.main != null) canvas.worldCamera = Camera.main;
        if (wasHidden && placement == Placement.InView && ResolveViewer())
        {
            // Appear right in front; later selections keep the current spot so the panel does not jump.
            canvas.transform.position = ViewTarget();
            following = false;
        }
        LateUpdate();
    }

    // ---- Content ---------------------------------------------------------

    private void ShowNode(GraphNode node)
    {
        Present("node:" + node.id, w => BuildNode(node, w), () => node.transform.position + Vector3.up * node.transform.lossyScale.y * 0.5f);
    }

    private void ShowEdge(GraphEdge edge)
    {
        Present("edge:" + edge.sourceId + "|" + edge.targetId, w => BuildEdge(edge, w), () => edge.Midpoint);
    }

    private void ShowPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var key = new StringBuilder("path");
        foreach (GraphNode n in nodes) key.Append(':').Append(n.id);
        GraphNode to = nodes[nodes.Count - 1];
        Present(key.ToString(), w => BuildPath(nodes, edges, w), () => to.transform.position + Vector3.up * to.transform.lossyScale.y * 0.5f);
    }

    private void BuildNode(GraphNode node, float w)
    {
        NodeData d = node.data;
        bool rich = d != null && (d.departures > 0 || d.arrivals > 0);
        Header("AIRPORT", ShortName(node), Join(" · ", Place(node), node.ShortCode != node.id ? node.ShortCode + " / " + node.id : node.id), w);

        var tiles = new List<(string, string, Color)> { ValueTile(node.value, d != null ? d.monthly : null, valueUnit) };
        if (rich)
        {
            tiles.Add((Delay(d.avgArrDelayMin), "avg arrival delay", UIKit.TextColor));
            tiles.Add((Pct(d.cargoShare), "cargo flights", UIKit.TextColor));
        }
        else
        {
            tiles.Add((graph.EdgesOf(node.id).Count.ToString(Inv), "routes", UIKit.TextColor));
        }
        Tiles(tiles, w);

        var lines = new List<string>();
        if (rich)
        {
            lines.Add("Departures <b>" + Big(d.departures) + "</b>  ·  arrivals <b>" + Big(d.arrivals) + "</b>");
            lines.Add("Departure delay <b>" + Delay(d.avgDepDelayMin) + "</b>  ·  scheduled <b>" + Pct(d.scheduledShare) + "</b>");
            string top = TopLine(d.topOperator, d.topAcType);
            if (top != null) lines.Add(top);
            string segments = SegmentLine(d.segments);
            if (segments != null) lines.Add(segments);
        }
        if (graph.colorByCommunity)
        {
            string hex = ColorUtility.ToHtmlStringRGB(graph.CommunityColor(node.community));
            lines.Add("<color=#" + hex + "><b>Cluster " + (node.community + 1) + "</b></color>  " + graph.CommunityName(node.community) +
                      "  <color=#9BA3B4>(" + graph.CommunityMembers(node.community).Count + " airports)</color>");
        }
        Lines(lines, w);

        // Busiest routes in the current view (month / filter).
        var routes = new List<GraphEdge>();
        foreach (GraphEdge e in graph.EdgesOf(node.id))
        {
            if (graph.FilteredWeight(e) > 0) routes.Add(e);
        }
        routes.Sort((a, b) => graph.FilteredWeight(b).CompareTo(graph.FilteredWeight(a)));
        int count = Mathf.Min(topNeighbours, routes.Count);
        if (count > 0)
        {
            var rows = new List<(string, string)>();
            for (int i = 0; i < count; i++)
            {
                GraphEdge e = routes[i];
                GraphNode other = graph.Nodes[e.sourceId == node.id ? e.targetId : e.sourceId];
                rows.Add(("<b>" + other.ShortCode + "</b>  " + Trim(City(other), 24), Num(graph.FilteredWeight(e))));
            }
            List("Busiest routes" + (graph.HasPeriod ? " in " + MonthLabel(graph.PeriodName(graph.Period)) : ""), rows, w);
        }
        Hint("Hold select on another airport (or point at it and press A / X) to see the route between them.", w);
    }

    private void BuildEdge(GraphEdge edge, float w)
    {
        GraphNode a = graph.Nodes[edge.sourceId];
        GraphNode b = graph.Nodes[edge.targetId];
        EdgeData d = edge.data;
        bool rich = d != null && d.forward + d.backward > 0;
        Header("ROUTE", a.ShortCode + "  -  " + b.ShortCode, Trim(City(a), 22) + "  -  " + Trim(City(b), 22), w);

        float km = GreatCircleKm(a.lat, a.lon, b.lat, b.lon);
        var tiles = new List<(string, string, Color)>
        {
            ValueTile(edge.weight, d != null ? d.monthly : null, weightUnit),
            (Num(km) + " km", "distance", UIKit.TextColor),
        };
        if (rich && d.avgDurationMin.HasValue) tiles.Add((Duration(d.avgDurationMin), "avg flight time", UIKit.TextColor));
        Tiles(tiles, w);

        var lines = new List<string> { "<b>" + ShortName(a) + "</b>  ·  <b>" + ShortName(b) + "</b>" };
        if (rich)
        {
            // forward = source -> target as stored in the file.
            lines.Add(a.ShortCode + " > " + b.ShortCode + " <b>" + Big(d.forward) + "</b>  ·  " +
                      b.ShortCode + " > " + a.ShortCode + " <b>" + Big(d.backward) + "</b>");
            string flown = d.avgDistanceNm.HasValue ? "  ·  flown <b>" + Num(d.avgDistanceNm.Value * 1.852f) + " km</b>" : "";
            lines.Add("Arrival delay <b>" + Delay(d.avgDelayMin) + "</b>" + flown);
            lines.Add("Cargo <b>" + Pct(d.cargoShare) + "</b>  ·  scheduled <b>" + Pct(d.scheduledShare) + "</b>");
            string top = TopLine(d.topOperator, d.topAcType);
            if (top != null) lines.Add(top);
            string segments = SegmentLine(d.segments);
            if (segments != null) lines.Add(segments);
        }
        Lines(lines, w);
    }

    private void BuildPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, float w)
    {
        GraphNode from = nodes[0];
        GraphNode to = nodes[nodes.Count - 1];
        int stops = edges.Count - 1;
        string via = "";
        for (int i = 1; i < nodes.Count - 1; i++) via += (i > 1 ? ", " : "") + nodes[i].ShortCode;
        Header("CONNECTION", from.ShortCode + "  >  " + to.ShortCode,
               stops == 0 ? "Direct connection" : "No direct flight  ·  via " + via, w);

        float km = 0f;
        for (int i = 0; i < nodes.Count - 1; i++) km += GreatCircleKm(nodes[i].lat, nodes[i].lon, nodes[i + 1].lat, nodes[i + 1].lon);
        float direct = GreatCircleKm(from.lat, from.lon, to.lat, to.lon);
        var tiles = new List<(string, string, Color)>
        {
            (stops == 0 ? "Direct" : stops + (stops == 1 ? " stop" : " stops"), stops == 0 ? "no change" : "fewest changes", stops == 0 ? UIKit.GoodColor : UIKit.HighlightColor),
            (Num(km) + " km", "flown distance", UIKit.TextColor),
        };
        if (stops > 0) tiles.Add((Num(direct) + " km", "as the crow flies", UIKit.TextColor));
        Tiles(tiles, w);

        var rows = new List<(string, string)>();
        for (int i = 0; i < edges.Count; i++)
        {
            rows.Add(("<b>" + nodes[i].ShortCode + " > " + nodes[i + 1].ShortCode + "</b>  " + Trim(City(nodes[i]), 14) + " - " + Trim(City(nodes[i + 1]), 14),
                      Num(graph.FilteredWeight(edges[i]) > 0 ? graph.FilteredWeight(edges[i]) : edges[i].weight)));
        }
        List(edges.Count == 1 ? "Flights on the route" : "Legs (flights)", rows, w);
        Lines(new List<string> { "<b>" + ShortName(from) + "</b>  to  <b>" + ShortName(to) + "</b>" }, w);
    }

    /// <summary>First tile: flights over all months, or in the timeline's month (highlighted).</summary>
    private (string, string, Color) ValueTile(int total, int[] monthly, string unit)
    {
        if (graph.HasPeriod && monthly != null && graph.Period < monthly.Length)
        {
            int count = monthly[graph.Period];
            return (Num(count), unit.ToLowerInvariant() + " in " + MonthLabel(graph.PeriodName(graph.Period)) +
                    " (" + Num((float)count / Mathf.Max(1, graph.CurrentDays)) + "/day)", UIKit.HighlightColor);
        }
        return (Big(total), unit.ToLowerInvariant() + " in total", UIKit.TextColor);
    }

    // ---- Building blocks (layout cursor y, canvas units) -----------------

    private void Header(string kind, string title, string subtitle, float w)
    {
        TextMeshProUGUI tag = UIKit.Text(content, kind, 13f, UIKit.AccentColor, Pad, y, w - 60f, 20f);
        tag.characterSpacing = 10f;
        tag.fontStyle = FontStyles.Bold;
        Button close = UIKit.Button(content, "", () => { if (selector != null) selector.ClearSelection(); },
                                    Pad + w - 40f, y - 6f, 40f, 40f, UIKit.ButtonStyle.Ghost, 18f, UIIcon.Shape.Close);
        close.name = "Close";
        y += 22f;
        y = Measured(title, 26f, UIKit.TextColor, w - 44f, FontStyles.Bold) + 2f;
        if (!string.IsNullOrEmpty(subtitle)) y = Measured(subtitle, 15f, UIKit.MutedTextColor, w, FontStyles.Normal) + 14f;
        else y += 12f;
    }

    private void Tiles(List<(string value, string label, Color color)> tiles, float w)
    {
        if (tiles.Count == 0) return;
        const float gap = 10f;
        const float h = 74f;
        float tw = (w - gap * (tiles.Count - 1)) / tiles.Count;
        for (int i = 0; i < tiles.Count; i++)
        {
            float x = Pad + i * (tw + gap);
            UIKit.Card(content, x, y, tw, h);
            TextMeshProUGUI v = UIKit.Text(content, tiles[i].value, 22f, tiles[i].color, x + 12f, y + 10f, tw - 20f, 30f);
            v.fontStyle = FontStyles.Bold;
            v.enableAutoSizing = true;
            v.fontSizeMin = 14f;
            v.fontSizeMax = 22f;
            v.textWrappingMode = TextWrappingModes.NoWrap;
            TextMeshProUGUI l = UIKit.Text(content, tiles[i].label, 13f, UIKit.MutedTextColor, x + 12f, y + 42f, tw - 20f, 30f);
            l.overflowMode = TextOverflowModes.Ellipsis;
        }
        y += h + 14f;
    }

    private void Lines(List<string> lines, float w)
    {
        if (lines.Count == 0) return;
        y = Measured(string.Join("\n", lines), 16f, UIKit.TextColor, w, FontStyles.Normal, 22f) + 12f;
    }

    private void List(string title, List<(string left, string right)> rows, float w)
    {
        UIKit.Divider(content, Pad, y, w);
        y += 10f;
        TextMeshProUGUI t = UIKit.Text(content, title, 13f, UIKit.MutedTextColor, Pad, y, w, 20f);
        t.characterSpacing = 4f;
        y += 24f;
        foreach ((string left, string right) in rows)
        {
            TextMeshProUGUI l = UIKit.Text(content, left, 16f, UIKit.TextColor, Pad, y, w - 110f, 24f);
            l.textWrappingMode = TextWrappingModes.NoWrap;
            UIKit.Text(content, right, 16f, UIKit.MutedTextColor, Pad + w - 110f, y, 110f, 24f, TextAlignmentOptions.TopRight);
            y += 26f;
        }
        y += 8f;
    }

    private void Hint(string text, float w)
    {
        if (!showHints) return;
        y = Measured(text, 13f, UIKit.MutedTextColor, w, FontStyles.Italic) + 4f;
    }

    /// <summary>Text at the cursor with its height measured (wrapping inside w); returns the y below it.</summary>
    private float Measured(string text, float size, Color color, float w, FontStyles style, float lineSpacing = 0f)
    {
        TextMeshProUGUI t = UIKit.Text(content, text, size, color, Pad, y, w, 10f);
        t.fontStyle = style;
        t.lineSpacing = lineSpacing;
        t.overflowMode = TextOverflowModes.Overflow;
        float h = t.GetPreferredValues(text, w, 0f).y;
        t.rectTransform.sizeDelta = new Vector2(w, h);
        return y + h;
    }

    // ---- Formatting ------------------------------------------------------

    /// <summary>"Istanbul, Turkey" from the node data; country or city alone if only one is known; "" if neither.</summary>
    private static string Place(GraphNode node)
    {
        NodeData d = node.data;
        if (d == null) return "";
        bool hasCity = !string.IsNullOrEmpty(d.city);
        bool hasCountry = !string.IsNullOrEmpty(d.country);
        if (hasCity && hasCountry) return d.city + ", " + d.country;
        return hasCity ? d.city : hasCountry ? d.country : "";
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

    private static string Join(string separator, params string[] parts)
    {
        var kept = new List<string>();
        foreach (string p in parts) if (!string.IsNullOrEmpty(p)) kept.Add(p);
        return string.Join(separator, kept);
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 2) + "..";
    }

    private static string Num(float v) => v.ToString("N0", Inv);

    /// <summary>2,236,446 -> "2.2 M"; 54,077 -> "54 k"; smaller numbers in full.</summary>
    private static string Big(long v)
    {
        if (v >= 1000000) return (v / 1000000f).ToString("0.0", Inv) + " M";
        if (v >= 10000) return (v / 1000f).ToString("0", Inv) + " k";
        return v.ToString("N0", Inv);
    }

    private static string Pct(float share) => (share * 100f).ToString("F0", Inv) + "%";

    private static readonly string[] MonthShort = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    /// <summary>"2020-04" -> "Apr 2020".</summary>
    private static string MonthLabel(string period)
    {
        if (period != null && period.Length >= 7 && int.TryParse(period.Substring(5, 2), out int month) && month >= 1 && month <= 12)
        {
            return MonthShort[month - 1] + " " + period.Substring(0, 4);
        }
        return period ?? "";
    }

    /// <summary>Signed minutes as "3 min late" / "2 min early" / "on time"; "n/a" when unknown.</summary>
    private static string Delay(float? minutes)
    {
        if (!minutes.HasValue) return "n/a";
        float m = Mathf.Round(minutes.Value);
        if (Mathf.Approximately(m, 0f)) return "on time";
        return Mathf.Abs(m).ToString("F0", Inv) + (m > 0f ? " min late" : " min early");
    }

    private static string Duration(float? minutes)
    {
        if (!minutes.HasValue) return "n/a";
        int total = Mathf.RoundToInt(minutes.Value);
        return total >= 60 ? (total / 60) + " h " + (total % 60).ToString("D2", Inv) : total + " min";
    }

    private static string TopLine(string op, string acType)
    {
        if (string.IsNullOrEmpty(op) && string.IsNullOrEmpty(acType)) return null;
        string s = "";
        if (!string.IsNullOrEmpty(op)) s += "Top operator <b>" + op + "</b>";
        if (!string.IsNullOrEmpty(op) && !string.IsNullOrEmpty(acType)) s += "  ·  ";
        if (!string.IsNullOrEmpty(acType)) s += "aircraft <b>" + acType + "</b>";
        return s;
    }

    /// <summary>Three biggest market segments with their share, e.g. "Mainline 87% · All-Cargo 6% · Lowcost 4%".</summary>
    private static string SegmentLine(Dictionary<string, int> segments)
    {
        if (segments == null || segments.Count == 0) return null;
        var sorted = new List<KeyValuePair<string, int>>(segments);
        sorted.Sort((x, y) => y.Value.CompareTo(x.Value));
        long total = 0;
        foreach (KeyValuePair<string, int> kv in sorted) total += kv.Value;
        if (total <= 0) return null;
        var parts = new List<string>();
        for (int i = 0; i < Mathf.Min(3, sorted.Count); i++) parts.Add(sorted[i].Key + " <b>" + Pct((float)sorted[i].Value / total) + "</b>");
        return "<color=#9BA3B4>" + string.Join("  ·  ", parts) + "</color>";
    }

    private static float GreatCircleKm(float lat1, float lon1, float lat2, float lon2)
    {
        const float earthRadiusKm = 6371f;
        float p1 = lat1 * Mathf.Deg2Rad;
        float p2 = lat2 * Mathf.Deg2Rad;
        float dp = (lat2 - lat1) * Mathf.Deg2Rad;
        float dl = (lon2 - lon1) * Mathf.Deg2Rad;
        float h = Mathf.Sin(dp / 2f) * Mathf.Sin(dp / 2f) +
                  Mathf.Cos(p1) * Mathf.Cos(p2) * Mathf.Sin(dl / 2f) * Mathf.Sin(dl / 2f);
        return 2f * earthRadiusKm * Mathf.Asin(Mathf.Min(1f, Mathf.Sqrt(h)));
    }
}
