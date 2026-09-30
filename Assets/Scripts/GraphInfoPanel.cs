using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// World-space info panel for the current selection, readable in the headset
/// where the Console is not visible. Always faces the camera. Placement:
/// - InView (default): in front of the viewer, slightly below the gaze; follows
///   the head lazily (moves once you turn or walk away from it, snaps after a
///   teleport), with a thin leader line to the selected node / edge;
/// - NearSelection: above the selected node (or the middle of the edge's arc),
///   pulled towards the viewer.
/// Built at runtime (TextMeshPro 3D text + background quad); no prefab needed.
/// The panel is not parented to the graph, so its size in metres does not depend
/// on the graph's scale.
/// </summary>
public class GraphInfoPanel : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Camera the panel faces. Defaults to Camera.main (the XR head camera).")]
    public Transform viewer;

    [Header("Text")]
    [Tooltip("What node.value means for the loaded data (OpenFlights: route-count degree; EUROCONTROL: flights).")]
    public string valueLabel = "Routes";
    [Tooltip("What edge.weight means for the loaded data (OpenFlights: distinct routes; EUROCONTROL: flights).")]
    public string weightLabel = "Routes";
    [Tooltip("Strongest neighbours listed for a selected node.")]
    public int topNeighbours = 3;

    public enum Placement { InView, NearSelection }

    [Header("Placement")]
    public Placement placement = Placement.InView;
    [Tooltip("InView: distance in front of the eyes (m).")]
    public float viewDistance = 0.8f;
    [Tooltip("InView: how far below the gaze line the panel's bottom edge sits (m).")]
    public float viewDownOffset = 0.25f;
    [Tooltip("InView: the panel starts following once it is this many degrees away from where you look.")]
    public float followAngle = 30f;
    [Tooltip("InView: the panel also starts following once its target spot is this far away (walking) (m).")]
    public float followDistance = 0.25f;
    [Tooltip("InView: beyond this (e.g. after a teleport) the panel jumps instead of gliding (m).")]
    public float snapDistance = 1.5f;
    [Tooltip("InView: follow smoothing; higher = snappier.")]
    public float followSpeed = 4f;
    [Tooltip("Farther than this from the selected node / edge, the panel closes and the selection is cleared (m). 0 = never.")]
    public float autoClearDistance = 5f;
    [Tooltip("Thin line from the panel to the selected node / edge.")]
    public bool showLeaderLine = true;
    public Color leaderLineColor = new Color(1f, 1f, 1f, 0.5f);
    public float leaderLineWidth = 0.002f;

    [Header("Layout (metres)")]
    [Tooltip("TextMeshPro 3D font size; 10 = 1 m line height.")]
    public float fontSize = 0.12f;
    public float panelWidth = 0.28f;
    public float padding = 0.01f;
    [Tooltip("NearSelection: gap between the selection and the bottom of the panel.")]
    public float verticalOffset = 0.04f;
    [Tooltip("NearSelection: pulls the panel from the selection towards the viewer, so it floats in front of the graph instead of among the edges.")]
    public float towardViewer = 0.3f;
    [Tooltip("NearSelection: the panel never comes closer to the eyes than this.")]
    public float minViewerDistance = 0.45f;
    public Color textColor = Color.white;
    public Color backgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
    [Tooltip("Background opacity: 0 = invisible, 1 = solid. Overrides the alpha of backgroundColor.")]
    [Range(0f, 1f)] public float backgroundAlpha = 0.55f;
    [Tooltip("Optional. If unset, a transparent Sprites/Default material is created (make sure the shader is included in builds).")]
    public Material backgroundMaterial;
    [Tooltip("Optional material for the leader line (vertex colours, e.g. Sprites/Default). If unset, Sprites/Default is created.")]
    public Material leaderLineMaterial;

    private Transform panel;
    private TextMeshPro text;
    private Transform background;
    private LineRenderer leaderLine;
    // Created here at runtime, so destroyed here too.
    private readonly List<Object> ownedAssets = new List<Object>();
    private Vector3 anchor;
    private bool following;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        BuildPanel();
    }

    private void OnEnable()
    {
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
        if (panel != null) Destroy(panel.gameObject);
    }

    private void OnDisable()
    {
        if (selector == null) return;
        selector.NodeSelected -= ShowNode;
        selector.EdgeSelected -= ShowEdge;
        selector.PathSelected -= ShowPath;
        selector.SelectionCleared -= Hide;
    }

    private void LateUpdate()
    {
        if (!panel.gameObject.activeSelf || !ResolveViewer()) return;

        if (autoClearDistance > 0f && Vector3.Distance(viewer.position, anchor) > autoClearDistance)
        {
            // Walked / teleported away from what the panel describes: drop it (Hide runs via SelectionCleared).
            selector.ClearSelection();
            return;
        }

        if (placement == Placement.InView)
        {
            FollowView();
        }
        else
        {
            panel.position = NearSelectionPosition();
        }

        // TMP text reads correctly when its +Z points away from the viewer.
        Vector3 away = panel.position - viewer.position;
        if (away.sqrMagnitude > 1e-6f) panel.rotation = Quaternion.LookRotation(away, Vector3.up);

        leaderLine.enabled = showLeaderLine;
        if (showLeaderLine)
        {
            leaderLine.SetPosition(0, panel.position);
            leaderLine.SetPosition(1, anchor);
        }
    }

    private bool ResolveViewer()
    {
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
        return viewer != null;
    }

    private Vector3 ViewTarget()
    {
        return viewer.position + viewer.forward * viewDistance - Vector3.up * viewDownOffset;
    }

    /// <summary>
    /// Lazy follow: stays put while you read it (small head motion does not move it),
    /// glides back in front once the head turns more than followAngle away or you
    /// walk more than followDistance, and jumps after a teleport.
    /// </summary>
    private void FollowView()
    {
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

    private Vector3 NearSelectionPosition()
    {
        Vector3 position = anchor + Vector3.up * verticalOffset;
        Vector3 toViewer = viewer.position - position;
        float distance = toViewer.magnitude;
        if (distance > 1e-4f)
        {
            float pull = Mathf.Clamp(distance - minViewerDistance, 0f, towardViewer);
            position += toViewer / distance * pull;
        }
        return position;
    }

    private void BuildPanel()
    {
        panel = new GameObject("InfoPanel").transform;

        GameObject textGo = new GameObject("Text");
        textGo.transform.SetParent(panel, false);
        text = textGo.AddComponent<TextMeshPro>();
        text.fontSize = fontSize;
        text.color = textColor;
        text.alignment = TextAlignmentOptions.BottomLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        // Pivot at bottom centre with zero height: text grows upwards from the anchor.
        text.rectTransform.pivot = new Vector2(0.5f, 0f);
        text.rectTransform.sizeDelta = new Vector2(panelWidth, 0f);

        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "Background";
        bg.transform.SetParent(panel, false);
        // The panel must never block XR rays aimed at nodes behind it.
        Destroy(bg.GetComponent<Collider>());
        Material mat = backgroundMaterial;
        if (mat == null)
        {
            // Sprites/Default alpha-blends out of the box (URP Unlit would need its transparent keywords set up).
            Color see = backgroundColor;
            see.a = backgroundAlpha;
            mat = new Material(Shader.Find("Sprites/Default")) { color = see };
            // Draw before the text (TMP uses queue 3000) so the see-through background never covers it.
            mat.renderQueue = 2990;
            ownedAssets.Add(mat);
        }
        Renderer bgRenderer = bg.GetComponent<Renderer>();
        bgRenderer.sharedMaterial = mat;
        bgRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        bgRenderer.receiveShadows = false;
        background = bg.transform;

        leaderLine = panel.gameObject.AddComponent<LineRenderer>();
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
            lineMat = new Material(Shader.Find("Sprites/Default"));
            ownedAssets.Add(lineMat);
        }
        leaderLine.sharedMaterial = lineMat;
        leaderLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        leaderLine.receiveShadows = false;

        panel.gameObject.SetActive(false);
    }

    private void ShowNode(GraphNode node)
    {
        IReadOnlyList<GraphEdge> edges = graph.EdgesOf(node.id);
        var neighbours = new List<GraphEdge>(edges);
        neighbours.Sort((x, y) => y.weight.CompareTo(x.weight));
        NodeData d = node.data;
        bool rich = d != null && (d.departures > 0 || d.arrivals > 0);

        var sb = new StringBuilder();
        sb.Append("<b>").Append(node.label).Append("</b>\n");
        sb.Append("<size=80%>");
        if (d != null && !string.IsNullOrEmpty(d.city))
        {
            sb.Append(d.city);
            if (!string.IsNullOrEmpty(d.country)) sb.Append(", ").Append(d.country);
            sb.Append(" · ");
        }
        sb.Append(node.id).Append(" · ").Append(node.lat.ToString("F2", Inv)).Append(", ").Append(node.lon.ToString("F2", Inv))
          .Append("</size>\n");

        sb.Append(valueLabel).Append(": <b>").Append(Num(node.value)).Append("</b>");
        if (rich)
        {
            sb.Append("  <size=80%>(").Append(Num(d.departures)).Append(" dep / ")
              .Append(Num(d.arrivals)).Append(" arr)</size>");
            sb.Append("\nAvg delay: dep <b>").Append(Delay(d.avgDepDelayMin))
              .Append("</b> · arr <b>").Append(Delay(d.avgArrDelayMin)).Append("</b>");
            sb.Append("\nCargo <b>").Append(Pct(d.cargoShare)).Append("</b> · Scheduled <b>")
              .Append(Pct(d.scheduledShare)).Append("</b>");
            AppendTop(sb, d.topOperator, d.topAcType);
            AppendSegments(sb, d.segments, node.value);
        }
        sb.Append("\nConnections shown: <b>").Append(edges.Count).Append("</b>");
        if (graph.colorByCommunity)
        {
            sb.Append(" · Cluster <b>").Append(node.community + 1).Append("</b>");
        }

        int count = Mathf.Min(topNeighbours, neighbours.Count);
        if (count > 0)
        {
            sb.Append("\n<size=80%>Top links:");
            for (int i = 0; i < count; i++)
            {
                GraphEdge e = neighbours[i];
                GraphNode other = graph.Nodes[e.sourceId == node.id ? e.targetId : e.sourceId];
                sb.Append("\n  ").Append(other.label).Append(" (").Append(Num(e.weight)).Append(')');
            }
            sb.Append("</size>");
        }

        Show(sb.ToString(), node.transform.position + Vector3.up * node.transform.lossyScale.y * 0.5f);
    }

    private void ShowEdge(GraphEdge edge)
    {
        GraphNode a = graph.Nodes[edge.sourceId];
        GraphNode b = graph.Nodes[edge.targetId];
        float km = GreatCircleKm(a.lat, a.lon, b.lat, b.lon);
        EdgeData d = edge.data;
        bool rich = d != null && d.forward + d.backward > 0;

        var sb = new StringBuilder();
        sb.Append("<b>").Append(a.label).Append("</b>\n");
        sb.Append("<size=80%><-></size>\n");
        sb.Append("<b>").Append(b.label).Append("</b>\n");
        sb.Append(weightLabel).Append(": <b>").Append(Num(edge.weight)).Append("</b>");
        if (rich)
        {
            // forward = source -> target as stored in the file.
            sb.Append("  <size=80%>(").Append(a.ShortCode).Append(">").Append(b.ShortCode).Append(' ').Append(Num(d.forward))
              .Append(" · ").Append(b.ShortCode).Append(">").Append(a.ShortCode).Append(' ').Append(Num(d.backward))
              .Append(")</size>");
        }
        sb.Append("\nDistance: <b>").Append(Num(km)).Append(" km</b>");
        if (rich)
        {
            if (d.avgDistanceNm.HasValue)
            {
                sb.Append(" <size=80%>(flown ").Append(Num(d.avgDistanceNm.Value * 1.852f)).Append(" km)</size>");
            }
            sb.Append("\nAvg duration: <b>").Append(Duration(d.avgDurationMin)).Append("</b> · delay <b>")
              .Append(Delay(d.avgDelayMin)).Append("</b>");
            sb.Append("\nCargo <b>").Append(Pct(d.cargoShare)).Append("</b> · Scheduled <b>")
              .Append(Pct(d.scheduledShare)).Append("</b>");
            AppendTop(sb, d.topOperator, d.topAcType);
            AppendSegments(sb, d.segments, edge.weight);
        }

        // Anchor on the top of the arc (middle point of the line).
        Show(sb.ToString(), edge.Midpoint);
    }

    private void ShowPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        GraphNode from = nodes[0];
        GraphNode to = nodes[nodes.Count - 1];

        var sb = new StringBuilder();
        sb.Append("<b>").Append(from.ShortCode).Append(" > ").Append(to.ShortCode).Append("</b>\n");
        sb.Append("<size=80%>").Append(from.label).Append("\n").Append(to.label).Append("</size>\n");

        if (edges.Count == 1)
        {
            sb.Append("<b>Direct connection</b> · ").Append(weightLabel).Append(": <b>")
              .Append(Num(edges[0].weight)).Append("</b>");
        }
        else
        {
            int stops = edges.Count - 1;
            sb.Append("No direct connection · <b>").Append(stops).Append(stops == 1 ? " stop" : " stops").Append("</b> via ");
            for (int i = 1; i < nodes.Count - 1; i++)
            {
                if (i > 1) sb.Append(", ");
                sb.Append(nodes[i].ShortCode);
            }
            sb.Append("\n<size=80%>");
            for (int i = 0; i < edges.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append("  ").Append(nodes[i].ShortCode).Append(" > ").Append(nodes[i + 1].ShortCode)
                  .Append(": ").Append(Num(edges[i].weight)).Append(' ').Append(weightLabel.ToLowerInvariant());
            }
            sb.Append("</size>");
        }

        float km = 0f;
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            km += GreatCircleKm(nodes[i].lat, nodes[i].lon, nodes[i + 1].lat, nodes[i + 1].lon);
        }
        sb.Append("\nDistance: <b>").Append(Num(km)).Append(" km</b>");
        if (edges.Count > 1)
        {
            sb.Append(" <size=80%>(direct ").Append(Num(GreatCircleKm(from.lat, from.lon, to.lat, to.lon))).Append(" km)</size>");
        }

        Show(sb.ToString(), to.transform.position + Vector3.up * to.transform.lossyScale.y * 0.5f);
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Num(float v) => v.ToString("N0", Inv);

    private static string Pct(float share) => (share * 100f).ToString("F0", Inv) + "%";

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
        return total >= 60 ? $"{total / 60} h {total % 60:D2} min" : $"{total} min";
    }

    private static void AppendTop(StringBuilder sb, string op, string acType)
    {
        if (string.IsNullOrEmpty(op) && string.IsNullOrEmpty(acType)) return;
        sb.Append("\n<size=80%>");
        if (!string.IsNullOrEmpty(op)) sb.Append("Top operator ").Append(op);
        if (!string.IsNullOrEmpty(op) && !string.IsNullOrEmpty(acType)) sb.Append(" · ");
        if (!string.IsNullOrEmpty(acType)) sb.Append("Top aircraft ").Append(acType);
        sb.Append("</size>");
    }

    /// <summary>Two biggest market segments with their share, e.g. "Mainline 87% · All-Cargo 6%".</summary>
    private static void AppendSegments(StringBuilder sb, Dictionary<string, int> segments, int total)
    {
        if (segments == null || segments.Count == 0 || total <= 0) return;
        var sorted = new List<KeyValuePair<string, int>>(segments);
        sorted.Sort((x, y) => y.Value.CompareTo(x.Value));
        sb.Append("\n<size=80%>");
        for (int i = 0; i < Mathf.Min(2, sorted.Count); i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(sorted[i].Key).Append(' ').Append(Pct((float)sorted[i].Value / total));
        }
        sb.Append("</size>");
    }

    private void Hide()
    {
        panel.gameObject.SetActive(false);
    }

    private void Show(string content, Vector3 worldAnchor)
    {
        anchor = worldAnchor;
        bool wasHidden = !panel.gameObject.activeSelf;
        panel.gameObject.SetActive(true);
        if (wasHidden && placement == Placement.InView && ResolveViewer())
        {
            // Appear right in front; later selections keep the current spot so the panel does not jump.
            panel.position = ViewTarget();
            following = false;
        }
        text.text = content;
        text.ForceMeshUpdate();

        // Fit the background to the rendered text (text-local space, same as panel space).
        Bounds bounds = text.textBounds;
        Vector3 textOffset = text.transform.localPosition;
        background.localPosition = textOffset + new Vector3(bounds.center.x, bounds.center.y, 0.002f);
        background.localScale = new Vector3(bounds.size.x + 2f * padding, bounds.size.y + 2f * padding, 1f);

        LateUpdate();
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
