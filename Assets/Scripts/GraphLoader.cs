using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

/// <summary>
/// Loads nodes.json / edges.json from StreamingAssets and renders the graph:
/// one sphere per node (sized by sqrt(value), coloured by value or community)
/// and one arc per undirected airport pair. All arcs live in a single mesh drawn
/// with the FlightlyVR/EdgeRibbon shader (camera-facing ribbons), so thousands of
/// edges cost one draw call and one GameObject. Nodes share one material per
/// colour instead of one material copy each.
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
    [Tooltip("How traffic maps to the low -> high colour gradient (nodes and edges). Rank spreads colours evenly; " +
             "Linear leaves almost everything at the low colour because a few hubs dwarf the rest.")]
    public ColorScale colorScale = ColorScale.Rank;

    public enum ColorScale { Linear, Sqrt, Rank }
    public Color[] communityColors =
    {
        new Color(0.30f, 0.69f, 0.96f), new Color(1.00f, 0.60f, 0.20f), new Color(0.45f, 0.85f, 0.45f),
        new Color(0.90f, 0.40f, 0.65f), new Color(0.75f, 0.65f, 1.00f), new Color(0.95f, 0.85f, 0.35f),
    };

    [Header("Edges")]
    [Tooltip("Material using the FlightlyVR/EdgeRibbon shader. Assign it so the shader is included in builds; if unset, the shader is looked up by name (Editor only works reliably).")]
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

    [Tooltip("AroundCenter only. GreatCircle: natural arcs over / under the viewer (arcs that would reach the floor are lifted smoothly so they just touch minEdgeHeight). Horizontal: arcs go round the viewer at the endpoints' heights.")]
    public AroundCenterPath aroundCenterPath = AroundCenterPath.GreatCircle;

    public enum AroundCenterPath { GreatCircle, Horizontal }

    [Tooltip("Lowest world height (m) any edge point may have; keeps edges above the floor (floor at y = 0).")]
    public float minEdgeHeight = 0.05f;

    [Header("Edge filtering / community colours")]
    [Tooltip("-1 = all edges. 0 = none at rest: edges appear only for the selection. N = the N most important edges (weight, then the smaller endpoint value, so hub-to-hub links win ties).")]
    public int maxVisibleEdges = -1;
    [Tooltip("With colorByCommunity: edges inside a community take its colour at this alpha.")]
    [Range(0f, 1f)] public float communityEdgeAlpha = 0.35f;
    [Tooltip("With colorByCommunity: colour of edges between two different communities.")]
    public Color interCommunityEdgeColor = new Color(0.7f, 0.7f, 0.7f, 0.08f);
    [Tooltip("Regional view: how many of the most important edges between communities stay visible (faint), next to the maxVisibleEdges edges inside communities.")]
    public int regionalInterEdges = 60;

    private const string EdgeShaderName = "FlightlyVR/EdgeRibbon";
    // Gradient colours are rounded to this many steps so nodes can share materials.
    private const int GradientSteps = 16;

    // Runtime state
    private readonly Dictionary<string, GraphNode> nodeInstances = new Dictionary<string, GraphNode>();
    private readonly List<GraphEdge> edgeInstances = new List<GraphEdge>();
    // Edges shown when nothing is selected (maxVisibleEdges + weight threshold).
    private readonly HashSet<GraphEdge> restVisibleEdges = new HashSet<GraphEdge>();
    private float edgeWeightThreshold = float.MinValue;
    private readonly Dictionary<string, List<GraphEdge>> edgesByNode = new Dictionary<string, List<GraphEdge>>();
    private Transform nodesRoot;

    // Shared node materials, one per displayed (HDR) colour.
    private readonly Dictionary<Color, Material> nodeMaterialCache = new Dictionary<Color, Material>();
    private Material nodeMaterialTemplate;

    // Edge mesh: per edge (builtSegments + 1) points, 2 vertices each.
    private GameObject edgesObject;
    private Mesh edgeMesh;
    private Material runtimeEdgeMaterial;
    private Vector2[] edgeUVs;
    private Color32[] edgeColors;
    private int builtSegments;
    private bool edgesDirty;

    public IReadOnlyDictionary<string, GraphNode> Nodes => nodeInstances;
    public IReadOnlyList<GraphEdge> Edges => edgeInstances;
    public int LoadedNodeCount => nodeInstances.Count;
    public int LoadedEdgeCount => edgeInstances.Count;
    public bool IsLoaded { get; private set; }
    /// <summary>Regional view (SetRegionalView): community colours, edges inside communities first.</summary>
    public bool RegionalView { get; private set; }

    private int maxNodeValue = 1;
    private int maxEdgeWeight = 1;
    // Sorted values, for the Rank colour scale.
    private int[] sortedNodeValues = System.Array.Empty<int>();
    private int[] sortedEdgeWeights = System.Array.Empty<int>();
    // Community id -> airports, biggest first (for names and labels).
    private readonly Dictionary<int, List<GraphNode>> communityMembers = new Dictionary<int, List<GraphNode>>();

    public IReadOnlyList<GraphEdge> EdgesOf(string nodeId)
    {
        return edgesByNode.TryGetValue(nodeId, out List<GraphEdge> list) ? list : System.Array.Empty<GraphEdge>();
    }

    private void Start()
    {
        StartCoroutine(LoadGraph());
    }

    private void LateUpdate()
    {
        if (edgesDirty && edgeMesh != null)
        {
            UploadEdgeDisplay();
        }
    }

    private void OnDestroy()
    {
        // Runtime-created assets are not scene objects: without this they pile up in the Editor every Play.
        foreach (Material m in nodeMaterialCache.Values)
        {
            Destroy(m);
        }
        nodeMaterialCache.Clear();
        if (runtimeEdgeMaterial != null) Destroy(runtimeEdgeMaterial);
        if (edgeMesh != null) Destroy(edgeMesh);
        if (edgesObject != null) Destroy(edgesObject);
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

        maxNodeValue = 1;
        sortedNodeValues = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            sortedNodeValues[i] = nodes[i].value;
            if (nodes[i].value > maxNodeValue)
            {
                maxNodeValue = nodes[i].value;
            }
        }
        System.Array.Sort(sortedNodeValues);

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

            Color nodeColor = NodeColor(node.value, node.community);
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                if (nodeMaterial != null) renderer.sharedMaterial = nodeMaterial;
                SetNodeColor(renderer, nodeColor);
            }

            GraphNode gn = go.AddComponent<GraphNode>();
            gn.baseColor = nodeColor;
            gn.data = node;
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
        maxEdgeWeight = 1;
        sortedEdgeWeights = new int[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            sortedEdgeWeights[i] = edges[i].weight;
            if (edges[i].weight > maxEdgeWeight)
            {
                maxEdgeWeight = edges[i].weight;
            }
        }
        System.Array.Sort(sortedEdgeWeights);

        builtSegments = edgeSegments;
        // Guard against duplicate pairs in either direction: one edge per airport pair.
        var seenPairs = new HashSet<(string, string)>();

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

            Color color = EdgeColor(edge.weight, a.community, b.community);

            var ge = new GraphEdge
            {
                sourceId = edge.source,
                targetId = edge.target,
                weight = edge.weight,
                baseColor = color,
                data = edge,
                points = new Vector3[builtSegments + 1],
                index = edgeInstances.Count,
                displayColor = color,
                displayWidth = edgeWidth,
            };
            FillArc(a.transform.position, b.transform.position, ge.points);
            for (int i = 0; i < ge.points.Length; i++)
            {
                // Safety net: never draw an edge below the floor.
                if (ge.points[i].y < minEdgeHeight) ge.points[i].y = minEdgeHeight;
            }

            edgeInstances.Add(ge);
            edgesByNode[edge.source].Add(ge);
            edgesByNode[edge.target].Add(ge);
        }

        BuildEdgeMesh();
    }

    /// <summary>
    /// One ribbon per edge in a single mesh. Vertex positions and arc directions are
    /// fixed; colours and widths (UV.y) are rewritten by UploadEdgeDisplay when the
    /// display state changes. The mesh object has an identity transform because the
    /// arc points are already in world space (like the former world-space LineRenderers).
    /// </summary>
    private void BuildEdgeMesh()
    {
        int pointsPerEdge = builtSegments + 1;
        int verticesPerEdge = pointsPerEdge * 2;
        int vertexCount = edgeInstances.Count * verticesPerEdge;

        var vertices = new Vector3[vertexCount];
        var directions = new Vector3[vertexCount];
        var indices = new int[edgeInstances.Count * builtSegments * 6];
        edgeUVs = new Vector2[vertexCount];
        edgeColors = new Color32[vertexCount];

        int vi = 0;
        int ii = 0;
        foreach (GraphEdge e in edgeInstances)
        {
            int first = vi;
            for (int p = 0; p < pointsPerEdge; p++)
            {
                Vector3 direction = e.points[Mathf.Min(p + 1, pointsPerEdge - 1)] - e.points[Mathf.Max(p - 1, 0)];
                for (int side = -1; side <= 1; side += 2)
                {
                    vertices[vi] = e.points[p];
                    directions[vi] = direction;
                    edgeUVs[vi] = new Vector2(side, 0f);
                    vi++;
                }
            }
            for (int seg = 0; seg < builtSegments; seg++)
            {
                // Point seg -> vertices v, v+1; point seg+1 -> v+2, v+3.
                int v = first + seg * 2;
                indices[ii++] = v;
                indices[ii++] = v + 2;
                indices[ii++] = v + 1;
                indices[ii++] = v + 1;
                indices[ii++] = v + 2;
                indices[ii++] = v + 3;
            }
        }

        edgeMesh = new Mesh { name = "Graph Edges" };
        edgeMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        edgeMesh.MarkDynamic();
        edgeMesh.vertices = vertices;
        edgeMesh.normals = directions;
        edgeMesh.SetUVs(0, edgeUVs);
        edgeMesh.colors32 = edgeColors;
        edgeMesh.SetIndices(indices, MeshTopology.Triangles, 0);
        edgeMesh.RecalculateBounds();
        // Ribbons are widened in the shader, beyond the centre-line bounds.
        Bounds bounds = edgeMesh.bounds;
        bounds.Expand(1f);
        edgeMesh.bounds = bounds;

        edgesObject = new GameObject("Edges (GraphLoader)");
        edgesObject.AddComponent<MeshFilter>().sharedMesh = edgeMesh;
        MeshRenderer meshRenderer = edgesObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = CreateEdgeMaterial();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        edgesDirty = true;
    }

    /// <summary>Runtime copy of the ribbon material with edgeIntensity applied to _Tint.</summary>
    private Material CreateEdgeMaterial()
    {
        Material source = edgeMaterial;
        if (source != null && source.shader.name != EdgeShaderName)
        {
            Debug.LogWarning($"GraphLoader: edgeMaterial '{source.name}' does not use {EdgeShaderName}; " +
                             "ignoring it. Create a material with that shader and assign it.");
            source = null;
        }

        if (source != null)
        {
            runtimeEdgeMaterial = new Material(source);
        }
        else
        {
            Shader shader = Shader.Find(EdgeShaderName);
            if (shader == null)
            {
                Debug.LogError($"GraphLoader: shader {EdgeShaderName} not found. Assign a material using it to edgeMaterial.");
                return null;
            }
            runtimeEdgeMaterial = new Material(shader);
        }

        runtimeEdgeMaterial.name = "Graph Edges (runtime)";
        Color tint = runtimeEdgeMaterial.GetColor("_Tint");
        runtimeEdgeMaterial.SetColor("_Tint", new Color(tint.r * edgeIntensity, tint.g * edgeIntensity, tint.b * edgeIntensity, tint.a));
        return runtimeEdgeMaterial;
    }

    /// <summary>Writes every edge's colour / width (0 when hidden) into the mesh.</summary>
    private void UploadEdgeDisplay()
    {
        int verticesPerEdge = (builtSegments + 1) * 2;
        var hidden = new Color32(0, 0, 0, 0);
        foreach (GraphEdge e in edgeInstances)
        {
            Color32 color = e.visible ? (Color32)e.displayColor : hidden;
            float width = e.visible ? e.displayWidth : 0f;
            int start = e.index * verticesPerEdge;
            for (int v = start; v < start + verticesPerEdge; v++)
            {
                edgeColors[v] = color;
                edgeUVs[v].y = width;
            }
        }
        edgeMesh.colors32 = edgeColors;
        edgeMesh.SetUVs(0, edgeUVs);
        edgesDirty = false;
    }

    /// <summary>Sets how an edge is drawn (colour may be dimmed / highlighted); uploaded at the end of the frame.</summary>
    public void SetEdgeDisplay(GraphEdge edge, Color color, float width)
    {
        edge.displayColor = color;
        edge.displayWidth = width;
        edgesDirty = true;
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
    /// Arc around this object's position (the viewer's head in the immersive view);
    /// the radius is interpolated between the endpoints, so no edge passes through the head.
    /// GreatCircle: the direction follows the shortest great circle, giving natural
    /// arcs over or under the viewer; an arc that would come closer to the floor than
    /// minEdgeHeight is lifted as a whole (see FillGreatCircle) so its lowest point
    /// just touches that height, a smooth tangent dip instead of sliding along the floor.
    /// Horizontal: azimuth and elevation are interpolated separately, so the arc
    /// never dips below its lower endpoint and goes round the viewer instead.
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
            // Endpoint at the centre: no direction to interpolate, fall back to a straight line.
            for (int i = 0; i <= n; i++) points[i] = Vector3.Lerp(a, b, (float)i / n);
            return;
        }

        if (aroundCenterPath == AroundCenterPath.GreatCircle)
        {
            Vector3 ua = da / ra;
            Vector3 ub = db / rb;
            if (FillGreatCircle(center, ua, ub, ra, rb, 0f, points) < minEdgeHeight)
            {
                // Smallest lift (binary search) that keeps the whole arc at or above minEdgeHeight:
                // the lowest point then just touches it.
                float low = 0f;
                float high = Mathf.PI * 0.5f;
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    float mid = (low + high) * 0.5f;
                    if (FillGreatCircle(center, ua, ub, ra, rb, mid, points) >= minEdgeHeight) high = mid;
                    else low = mid;
                }
                FillGreatCircle(center, ua, ub, ra, rb, high, points);
            }
            return;
        }

        float elevationA = Mathf.Asin(Mathf.Clamp(da.y / ra, -1f, 1f));
        float elevationB = Mathf.Asin(Mathf.Clamp(db.y / rb, -1f, 1f));
        float azimuthA = Mathf.Atan2(da.x, da.z);
        float azimuthB = Mathf.Atan2(db.x, db.z);
        float deltaAzimuth = Mathf.DeltaAngle(azimuthA * Mathf.Rad2Deg, azimuthB * Mathf.Rad2Deg) * Mathf.Deg2Rad;

        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            float radius = Mathf.Lerp(ra, rb, t);
            float elevation = Mathf.Lerp(elevationA, elevationB, t);
            float azimuth = azimuthA + deltaAzimuth * t;
            float horizontal = radius * Mathf.Cos(elevation);
            points[i] = center + new Vector3(horizontal * Mathf.Sin(azimuth), radius * Mathf.Sin(elevation), horizontal * Mathf.Cos(azimuth));
        }
    }

    /// <summary>
    /// Great-circle arc from direction ua to ub (radius lerped from ra to rb) whose
    /// elevation is raised by lift * sin(pi * t): zero at both endpoints, largest in
    /// the middle, so the lifted arc stays smooth. Returns the lowest world y.
    /// </summary>
    private static float FillGreatCircle(Vector3 center, Vector3 ua, Vector3 ub, float ra, float rb, float lift, Vector3[] points)
    {
        int n = points.Length - 1;
        float lowest = float.PositiveInfinity;
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            float radius = Mathf.Lerp(ra, rb, t);
            Vector3 direction = Vector3.Slerp(ua, ub, t);
            float elevation = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) + lift * Mathf.Sin(Mathf.PI * t);
            float azimuth = Mathf.Atan2(direction.x, direction.z);
            float horizontal = radius * Mathf.Cos(elevation);
            points[i] = center + new Vector3(horizontal * Mathf.Sin(azimuth), radius * Mathf.Sin(elevation), horizontal * Mathf.Cos(azimuth));
            lowest = Mathf.Min(lowest, points[i].y);
        }
        return lowest;
    }

    /// <summary>
    /// Sets a node's display colour, applying nodeIntensity (HDR glow). Use this
    /// instead of writing material.color so highlight / dim keep the glow.
    /// </summary>
    public void SetNodeColor(Renderer renderer, Color color)
    {
        Color hdr = color * nodeIntensity;
        hdr.a = color.a;
        if (!nodeMaterialCache.TryGetValue(hdr, out Material material))
        {
            // Template: nodeMaterial, else the primitive's default material (captured before we replace it).
            if (nodeMaterialTemplate == null) nodeMaterialTemplate = nodeMaterial != null ? nodeMaterial : renderer.sharedMaterial;
            material = new Material(nodeMaterialTemplate) { name = "Graph Node " + ColorUtility.ToHtmlStringRGBA(color) };
            material.color = hdr;
            nodeMaterialCache.Add(hdr, material);
        }
        renderer.sharedMaterial = material;
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
            edge.visible = restVisibleEdges.Contains(edge);
        }
        edgesDirty = true;
    }

    /// <summary>Resting edge set plus `extra` (e.g. a selected node's edges, even if filtered out).</summary>
    public void RevealEdges(IEnumerable<GraphEdge> extra)
    {
        ResetEdgeVisibility();
        foreach (GraphEdge edge in extra)
        {
            edge.visible = true;
        }
    }

    private void ApplyEdgeFilter()
    {
        var ranked = new List<GraphEdge>(edgeInstances);
        ranked.Sort(CompareImportance);
        int limit = maxVisibleEdges < 0 ? ranked.Count : maxVisibleEdges;

        restVisibleEdges.Clear();
        if (RegionalView)
        {
            // Up to `limit` edges inside communities plus a few faint ones between them.
            int interLimit = maxVisibleEdges < 0 ? ranked.Count : regionalInterEdges;
            int intra = 0;
            int inter = 0;
            foreach (GraphEdge edge in ranked)
            {
                if (edge.weight < edgeWeightThreshold) continue;
                bool inside = nodeInstances[edge.sourceId].community == nodeInstances[edge.targetId].community;
                if (inside && intra < limit)
                {
                    restVisibleEdges.Add(edge);
                    intra++;
                }
                else if (!inside && inter < interLimit)
                {
                    restVisibleEdges.Add(edge);
                    inter++;
                }
            }
        }
        else
        {
            foreach (GraphEdge edge in ranked)
            {
                if (restVisibleEdges.Count >= limit) break;
                if (edge.weight >= edgeWeightThreshold) restVisibleEdges.Add(edge);
            }
        }
        ResetEdgeVisibility();
    }

    /// <summary>
    /// Switches between the top-routes view (value / weight colours, most important
    /// edges) and the regional view (community colours, edges inside communities,
    /// a few faint ones between them). Recolours and refilters in place; the caller
    /// should clear the selection first so no highlight is left on old colours.
    /// </summary>
    public void SetRegionalView(bool regional)
    {
        RegionalView = regional;
        colorByCommunity = regional;
        foreach (GraphNode node in nodeInstances.Values)
        {
            node.baseColor = NodeColor(node.value, node.community);
            Renderer renderer = node.GetComponent<Renderer>();
            if (renderer != null) SetNodeColor(renderer, node.baseColor);
        }
        foreach (GraphEdge edge in edgeInstances)
        {
            edge.baseColor = EdgeColor(edge.weight, nodeInstances[edge.sourceId].community, nodeInstances[edge.targetId].community);
            SetEdgeDisplay(edge, edge.baseColor, edgeWidth);
        }
        ApplyEdgeFilter();
    }

    /// <summary>Distinct community ids, largest community first.</summary>
    public IReadOnlyList<int> Communities
    {
        get
        {
            BuildCommunityIndex();
            var ids = new List<int>(communityMembers.Keys);
            ids.Sort((x, y) => communityMembers[y].Count != communityMembers[x].Count
                ? communityMembers[y].Count.CompareTo(communityMembers[x].Count)
                : x.CompareTo(y));
            return ids;
        }
    }

    /// <summary>Airports of a community, biggest (by value) first.</summary>
    public IReadOnlyList<GraphNode> CommunityMembers(int community)
    {
        BuildCommunityIndex();
        return communityMembers.TryGetValue(community, out List<GraphNode> list) ? list : (IReadOnlyList<GraphNode>)System.Array.Empty<GraphNode>();
    }

    /// <summary>Automatic community name from its biggest airports, e.g. "IST · SAW · VIE".</summary>
    public string CommunityName(int community, int airports = 3)
    {
        IReadOnlyList<GraphNode> members = CommunityMembers(community);
        var codes = new List<string>();
        for (int i = 0; i < members.Count && i < airports; i++) codes.Add(members[i].ShortCode);
        return string.Join(" · ", codes);
    }

    public Color CommunityColor(int community)
    {
        return communityColors.Length > 0 ? communityColors[Mathf.Abs(community) % communityColors.Length] : Color.white;
    }

    private void BuildCommunityIndex()
    {
        if (communityMembers.Count > 0 || nodeInstances.Count == 0) return;
        foreach (GraphNode node in nodeInstances.Values)
        {
            if (!communityMembers.TryGetValue(node.community, out List<GraphNode> list))
            {
                list = new List<GraphNode>();
                communityMembers.Add(node.community, list);
            }
            list.Add(node);
        }
        foreach (List<GraphNode> list in communityMembers.Values)
        {
            list.Sort((x, y) => x.value != y.value ? y.value.CompareTo(x.value) : string.CompareOrdinal(x.id, y.id));
        }
    }

    /// <summary>Community colour, or the value gradient (rounded so nodes can share materials).</summary>
    private Color NodeColor(int value, int community)
    {
        if (colorByCommunity && communityColors.Length > 0) return CommunityColor(community);
        float gradient = Mathf.Round(GradientPosition(value, maxNodeValue, sortedNodeValues) * (GradientSteps - 1)) / (GradientSteps - 1);
        return Color.Lerp(nodeLowColor, nodeHighColor, gradient);
    }

    /// <summary>Community colour inside a community / faint between them, or the weight gradient.</summary>
    private Color EdgeColor(int weight, int communityA, int communityB)
    {
        if (colorByCommunity && communityColors.Length > 0)
        {
            if (communityA != communityB) return interCommunityEdgeColor;
            Color color = CommunityColor(communityA);
            color.a = communityEdgeAlpha;
            return color;
        }
        return Color.Lerp(edgeLowColor, edgeHighColor, GradientPosition(weight, maxEdgeWeight, sortedEdgeWeights));
    }

    /// <summary>
    /// Position 0..1 of a value on the colour gradient. Rank: share of items smaller
    /// than it (equal values get the same colour), so colours spread evenly however
    /// skewed the traffic is. Sqrt / Linear: value relative to the maximum.
    /// </summary>
    private float GradientPosition(int value, int max, int[] sorted)
    {
        switch (colorScale)
        {
            case ColorScale.Linear:
                return max > 0 ? Mathf.Clamp01((float)value / max) : 0f;
            case ColorScale.Sqrt:
                return max > 0 ? Mathf.Sqrt(Mathf.Clamp01((float)value / max)) : 0f;
            default:
                if (sorted.Length < 2) return 0f;
                int index = System.Array.BinarySearch(sorted, value);
                if (index < 0) index = ~index;
                while (index > 0 && sorted[index - 1] == value) index--;
                return (float)index / (sorted.Length - 1);
        }
    }

    /// <summary>Heavier first; ties: the edge whose weaker endpoint is bigger (hub-to-hub first); then file order for stability.</summary>
    private int CompareImportance(GraphEdge x, GraphEdge y)
    {
        int c = y.weight.CompareTo(x.weight);
        if (c != 0) return c;
        c = WeakerEndpointValue(y).CompareTo(WeakerEndpointValue(x));
        if (c != 0) return c;
        return x.index.CompareTo(y.index);
    }

    private int WeakerEndpointValue(GraphEdge e)
    {
        return Mathf.Min(nodeInstances[e.sourceId].value, nodeInstances[e.targetId].value);
    }
}
