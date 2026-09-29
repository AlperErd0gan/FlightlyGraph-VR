using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Loads nodes.json / edges.json from StreamingAssets and renders the graph:
/// one sphere per node (sized by sqrt(value), coloured by value) and one arced
/// LineRenderer per undirected airport pair. Edges lift above the map so they do
/// not overlap the nodes when viewed from above.
/// Both files are always read through UnityWebRequest so the same code path
/// works in the Editor, on desktop and on Android/Quest (where StreamingAssets
/// lives inside the APK and plain File I/O fails).
/// </summary>
public class GraphLoader : MonoBehaviour
{
    [Header("Files (relative to Application.streamingAssetsPath)")]
    public string nodesFileName = "nodes.json";
    public string edgesFileName = "edges.json";

    [Header("Nodes")]
    [Tooltip("Optional. If unset, a sphere primitive is created per node.")]
    public GameObject nodePrefab;
    [Tooltip("Optional material for nodes (e.g. URP Unlit for a neon look). If unset, the sphere's default Lit material is kept.")]
    public Material nodeMaterial;
    [Tooltip("Node colour multiplier. >1 gives HDR colours that glow with Bloom (best with an Unlit nodeMaterial).")]
    public float nodeIntensity = 1f;
    [Tooltip("Multiplier applied to the (x, y, z) read from nodes.json. Node sizes are NOT scaled, so a larger value spreads dense regions (Europe) apart.")]
    public float positionScale = 4f;
    [Tooltip("Multiplier for the altitude-derived y. 0 = flat on the map (recommended with MapPlane).")]
    public float altitudeScale = 0f;
    [Tooltip("Sphere diameter = nodeBaseSize + nodeSizePerSqrtValue * sqrt(value).")]
    public float nodeBaseSize = 0.04f;
    public float nodeSizePerSqrtValue = 0.006f;
    public Color nodeLowColor = new Color(0.35f, 0.65f, 1f);
    public Color nodeHighColor = new Color(1f, 0.55f, 0.15f);
    [Tooltip("Colour nodes by their `community` field (layout_3d.py) instead of the value gradient.")]
    public bool colorByCommunity = false;
    public Color[] communityColors =
    {
        new Color(0.30f, 0.69f, 0.96f), new Color(1.00f, 0.60f, 0.20f), new Color(0.45f, 0.85f, 0.45f),
        new Color(0.90f, 0.40f, 0.65f), new Color(0.75f, 0.65f, 1.00f), new Color(0.95f, 0.85f, 0.35f),
    };

    [Header("Edges")]
    [Tooltip("Optional. If unset, Sprites/Default is used (supports vertex colours + alpha).")]
    public Material edgeMaterial;
    [Tooltip("Edge colour multiplier. >1 gives HDR edges that glow with Bloom. Applied to a runtime copy of the edge material.")]
    public float edgeIntensity = 1f;
    public float edgeWidth = 0.012f;
    [Tooltip("Arc peak height as a fraction of the chord length.")]
    public float edgeArcHeight = 0.18f;
    [Range(2, 32)]
    public int edgeSegments = 12;
    public Color edgeLowColor = new Color(0.5f, 0.7f, 1f, 0.25f);
    public Color edgeHighColor = new Color(1f, 0.45f, 0.2f, 0.6f);
    [Tooltip("LiftedArc: map view, arcs rise along +Y. AroundCenter: immersive view, arcs bend around this object's position so no edge passes through the viewer's head.")]
    public EdgeShape edgeShape = EdgeShape.LiftedArc;

    public enum EdgeShape { LiftedArc, AroundCenter }

    [Header("Edge filtering / community colours")]
    [Tooltip("-1 = all edges. 0 = none at rest: edges appear only for the selection. N = the N most important edges (weight, then the smaller endpoint value, so hub-to-hub links win ties).")]
    public int maxVisibleEdges = -1;
    [Tooltip("With colorByCommunity: edges inside a community take its colour at this alpha.")]
    [Range(0f, 1f)] public float communityEdgeAlpha = 0.35f;
    [Tooltip("With colorByCommunity: colour of edges between two different communities.")]
    public Color interCommunityEdgeColor = new Color(0.7f, 0.7f, 0.7f, 0.08f);

    // Runtime state
    private readonly Dictionary<string, GraphNode> nodeInstances = new Dictionary<string, GraphNode>();
    private readonly List<GraphEdge> edgeInstances = new List<GraphEdge>();
    // Edges shown when nothing is selected (maxVisibleEdges + weight threshold).
    private readonly HashSet<GraphEdge> restVisibleEdges = new HashSet<GraphEdge>();
    private float edgeWeightThreshold = float.MinValue;
    private readonly Dictionary<string, List<GraphEdge>> edgesByNode = new Dictionary<string, List<GraphEdge>>();
    private Transform nodesRoot;
    private Transform edgesRoot;

    public IReadOnlyDictionary<string, GraphNode> Nodes => nodeInstances;
    public IReadOnlyList<GraphEdge> Edges => edgeInstances;
    public int LoadedNodeCount => nodeInstances.Count;
    public int LoadedEdgeCount => edgeInstances.Count;
    public bool IsLoaded { get; private set; }

    public IReadOnlyList<GraphEdge> EdgesOf(string nodeId)
    {
        return edgesByNode.TryGetValue(nodeId, out List<GraphEdge> list) ? list : System.Array.Empty<GraphEdge>();
    }

    private void Start()
    {
        StartCoroutine(LoadGraph());
    }

    private IEnumerator LoadGraph()
    {
        string nodesJson = null;
        string edgesJson = null;

        yield return ReadStreamingAsset(nodesFileName, text => nodesJson = text);
        if (nodesJson == null)
        {
            yield break; // error already logged
        }

        yield return ReadStreamingAsset(edgesFileName, text => edgesJson = text);
        if (edgesJson == null)
        {
            yield break;
        }

        // No yield inside try/catch: C# does not allow yield statements in catch blocks.
        List<NodeData> nodes = null;
        List<EdgeData> edges = null;
        string parseError = null;
        try
        {
            nodes = JsonConvert.DeserializeObject<List<NodeData>>(nodesJson);
            edges = JsonConvert.DeserializeObject<List<EdgeData>>(edgesJson);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }

        if (parseError != null)
        {
            Debug.LogError($"GraphLoader: JSON parse failed: {parseError}");
            yield break;
        }
        if (nodes == null || edges == null)
        {
            Debug.LogError("GraphLoader: deserialized null (empty or malformed file).");
            yield break;
        }

        BuildNodes(nodes);
        BuildEdges(edges);
        ApplyEdgeFilter();
        IsLoaded = true;

        Debug.Log($"GraphLoader: loaded {nodeInstances.Count} nodes, {edgeInstances.Count} edges " +
                  $"({nodes.Count} nodes / {edges.Count} edges in files).");
    }

    /// <summary>
    /// Reads a file under StreamingAssets via UnityWebRequest. On success invokes
    /// onText with the file contents; on failure logs an error and leaves it uncalled.
    /// </summary>
    private IEnumerator ReadStreamingAsset(string fileName, System.Action<string> onText)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        // On desktop/Editor streamingAssetsPath is a bare filesystem path; UnityWebRequest
        // needs a URI. On Android it is already a jar:file:// URI and must be left alone.
        string uri = path.Contains("://") ? path : "file://" + path;

        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"GraphLoader: failed to read '{uri}': {request.error}");
                yield break;
            }

            onText(request.downloadHandler.text);
        }
    }

    private void BuildNodes(List<NodeData> nodes)
    {
        nodesRoot = new GameObject("Nodes").transform;
        nodesRoot.SetParent(transform, false);

        int maxValue = 1;
        foreach (NodeData node in nodes)
        {
            if (node.value > maxValue)
            {
                maxValue = node.value;
            }
        }

        foreach (NodeData node in nodes)
        {
            if (string.IsNullOrEmpty(node.id))
            {
                Debug.LogWarning("GraphLoader: node with empty id skipped.");
                continue;
            }
            if (nodeInstances.ContainsKey(node.id))
            {
                Debug.LogWarning($"GraphLoader: duplicate node id '{node.id}' skipped.");
                continue;
            }

            GameObject go;
            if (nodePrefab != null)
            {
                go = Instantiate(nodePrefab, nodesRoot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(nodesRoot, false);
            }

            go.name = string.IsNullOrEmpty(node.label) ? node.id : node.label;
            go.transform.localPosition = new Vector3(node.x, node.y * altitudeScale, node.z) * positionScale;
            float size = nodeBaseSize + nodeSizePerSqrtValue * Mathf.Sqrt(node.value);
            go.transform.localScale = Vector3.one * size;

            Color nodeColor = colorByCommunity && communityColors.Length > 0
                ? communityColors[Mathf.Abs(node.community) % communityColors.Length]
                : Color.Lerp(nodeLowColor, nodeHighColor, (float)node.value / maxValue);
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                if (nodeMaterial != null) renderer.sharedMaterial = nodeMaterial;
                SetNodeColor(renderer, nodeColor);
            }

            GraphNode gn = go.AddComponent<GraphNode>();
            gn.baseColor = nodeColor;
            gn.id = node.id;
            gn.label = node.label;
            gn.value = node.value;
            gn.lat = node.lat;
            gn.lon = node.lon;
            gn.community = node.community;

            nodeInstances.Add(node.id, gn);
            edgesByNode[node.id] = new List<GraphEdge>();
        }
    }

    private void BuildEdges(List<EdgeData> edges)
    {
        edgesRoot = new GameObject("Edges").transform;
        edgesRoot.SetParent(transform, false);

        Material material = edgeMaterial != null ? edgeMaterial : new Material(Shader.Find("Sprites/Default"));
        if (!Mathf.Approximately(edgeIntensity, 1f))
        {
            // Vertex colours are clamped to [0, 1], so the HDR boost goes on the material tint.
            // Runtime copy: never modify the shared material asset.
            material = new Material(material);
            string tint = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            if (material.HasProperty(tint))
            {
                Color c = material.GetColor(tint);
                material.SetColor(tint, new Color(c.r * edgeIntensity, c.g * edgeIntensity, c.b * edgeIntensity, c.a));
            }
        }

        int maxWeight = 1;
        foreach (EdgeData edge in edges)
        {
            if (edge.weight > maxWeight)
            {
                maxWeight = edge.weight;
            }
        }

        // Guard against duplicate pairs in either direction: one edge per airport pair.
        var seenPairs = new HashSet<(string, string)>();
        var points = new Vector3[edgeSegments + 1];

        foreach (EdgeData edge in edges)
        {
            GraphNode a;
            GraphNode b;
            if (!nodeInstances.TryGetValue(edge.source, out a) ||
                !nodeInstances.TryGetValue(edge.target, out b))
            {
                Debug.LogWarning($"GraphLoader: edge {edge.source} -> {edge.target} references unknown node; skipped.");
                continue;
            }
            var pair = string.CompareOrdinal(edge.source, edge.target) < 0
                ? (edge.source, edge.target)
                : (edge.target, edge.source);
            if (!seenPairs.Add(pair))
            {
                continue;
            }

            GameObject go = new GameObject($"Edge {edge.source}-{edge.target}");
            go.transform.SetParent(edgesRoot, false);

            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.sharedMaterial = material;
            line.startWidth = edgeWidth;
            line.endWidth = edgeWidth;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            FillArc(a.transform.position, b.transform.position, points);
            line.positionCount = points.Length;
            line.SetPositions(points);

            float t = maxWeight > 1 ? (edge.weight - 1f) / (maxWeight - 1f) : 0f;
            Color color = Color.Lerp(edgeLowColor, edgeHighColor, t);
            if (colorByCommunity && communityColors.Length > 0)
            {
                GraphNode na = nodeInstances[edge.source];
                GraphNode nb = nodeInstances[edge.target];
                if (na.community == nb.community)
                {
                    color = communityColors[Mathf.Abs(na.community) % communityColors.Length];
                    color.a = communityEdgeAlpha;
                }
                else
                {
                    color = interCommunityEdgeColor;
                }
            }
            line.startColor = color;
            line.endColor = color;

            GraphEdge ge = go.AddComponent<GraphEdge>();
            ge.sourceId = edge.source;
            ge.targetId = edge.target;
            ge.weight = edge.weight;
            ge.line = line;
            ge.baseColor = color;

            edgeInstances.Add(ge);
            edgesByNode[edge.source].Add(ge);
            edgesByNode[edge.target].Add(ge);
        }
    }

    /// <summary>Quadratic Bézier from a to b whose midpoint is lifted along +Y.</summary>
    private void FillArc(Vector3 a, Vector3 b, Vector3[] points)
    {
        if (edgeShape == EdgeShape.AroundCenter)
        {
            FillArcAroundCenter(a, b, points);
            return;
        }

        float chord = Vector3.Distance(a, b);
        Vector3 control = (a + b) * 0.5f + Vector3.up * (chord * edgeArcHeight * 2f);
        int n = points.Length - 1;
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            float u = 1f - t;
            points[i] = u * u * a + 2f * u * t * control + t * t * b;
        }
    }

    /// <summary>
    /// Arc that keeps its distance from this object's position: direction is
    /// slerped and radius lerped between the endpoints, so with the viewer at the
    /// centre an edge between opposite sides goes around the head, not through it.
    /// </summary>
    private void FillArcAroundCenter(Vector3 a, Vector3 b, Vector3[] points)
    {
        Vector3 center = transform.position;
        Vector3 da = a - center;
        Vector3 db = b - center;
        float ra = da.magnitude;
        float rb = db.magnitude;
        int n = points.Length - 1;
        if (ra < 1e-4f || rb < 1e-4f)
        {
            // Endpoint at the centre: no direction to slerp, fall back to a straight line.
            for (int i = 0; i <= n; i++) points[i] = Vector3.Lerp(a, b, (float)i / n);
            return;
        }
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            points[i] = center + Vector3.Slerp(da / ra, db / rb, t) * Mathf.Lerp(ra, rb, t);
        }
    }

    /// <summary>
    /// Sets a node's display colour, applying nodeIntensity (HDR glow). Use this
    /// instead of writing material.color so highlight / dim keep the glow.
    /// </summary>
    public void SetNodeColor(Renderer renderer, Color color)
    {
        Color hdr = color * nodeIntensity;
        hdr.a = color.a;
        renderer.material.color = hdr;
    }

    /// <summary>Shows only edges whose weight is >= threshold.</summary>
    public void SetEdgeWeightThreshold(float threshold)
    {
        edgeWeightThreshold = threshold;
        ApplyEdgeFilter();
    }

    /// <summary>Back to the resting edge set (used when the selection is cleared).</summary>
    public void ResetEdgeVisibility()
    {
        foreach (GraphEdge edge in edgeInstances)
        {
            edge.gameObject.SetActive(restVisibleEdges.Contains(edge));
        }
    }

    /// <summary>Resting edge set plus `extra` (e.g. a selected node's edges, even if filtered out).</summary>
    public void RevealEdges(IEnumerable<GraphEdge> extra)
    {
        ResetEdgeVisibility();
        foreach (GraphEdge edge in extra)
        {
            edge.gameObject.SetActive(true);
        }
    }

    private void ApplyEdgeFilter()
    {
        var ranked = new List<GraphEdge>(edgeInstances);
        ranked.Sort(CompareImportance);
        int limit = maxVisibleEdges < 0 ? ranked.Count : maxVisibleEdges;

        restVisibleEdges.Clear();
        foreach (GraphEdge edge in ranked)
        {
            if (restVisibleEdges.Count >= limit) break;
            if (edge.weight >= edgeWeightThreshold) restVisibleEdges.Add(edge);
        }
        ResetEdgeVisibility();
    }

    /// <summary>Heavier first; ties: the edge whose weaker endpoint is bigger (hub-to-hub first); then by name for stability.</summary>
    private int CompareImportance(GraphEdge x, GraphEdge y)
    {
        int c = y.weight.CompareTo(x.weight);
        if (c != 0) return c;
        c = WeakerEndpointValue(y).CompareTo(WeakerEndpointValue(x));
        if (c != 0) return c;
        return string.CompareOrdinal(x.name, y.name);
    }

    private int WeakerEndpointValue(GraphEdge e)
    {
        return Mathf.Min(nodeInstances[e.sourceId].value, nodeInstances[e.targetId].value);
    }
}
