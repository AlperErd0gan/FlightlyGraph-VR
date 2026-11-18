using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The start screen's surroundings: while the start panel is open the network floats around the user as a
/// slowly turning shell of glowing airports with light pulses running along its routes (the loaded
/// graph, rebuilt around the head). It is also the loading screen for the voice assistant: StartPanel
/// feeds <see cref="Progress"/> (0 = loading, 1 = everything ready) and the pulses speed up and brighten with it.
/// When the user starts, the shell opens outwards and fades away. Self-contained: StartPanel adds it at runtime.
/// </summary>
public class StartAmbience : MonoBehaviour
{
    [Header("Shell")]
    [Tooltip("Distance (m) of the nearest and the farthest airports from the user's head.")]
    public Vector2 radiusRange = new Vector2(1.9f, 3.4f);
    [Tooltip("Busiest routes drawn as arcs.")]
    public int routeCount = 150;
    [Tooltip("Size (m) of an airport at the nearest distance (grows with distance so all look alike).")]
    public float airportSize = 0.035f;

    [Header("Motion")]
    [Tooltip("Turning speed (deg/s) of the shell around the vertical axis.")]
    public float spin = 5f;
    [Tooltip("Seconds to fade in, and to open outwards and fade out.")]
    public float fadeInSeconds = 1.6f;
    public float fadeOutSeconds = 1.0f;

    /// <summary>0 = loading, 1 = ready: pulses run faster and the airports glow brighter.</summary>
    public float Progress { get; set; }

    /// <summary>The shell is wanted (the start screen is open before the first start).</summary>
    public bool Wanted { get; set; }

    private const int ArcSamples = 16;
    // Octahedron around an airport: 6 corners, 8 faces.
    private static readonly Vector3[] Corners =
    {
        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
    };
    private static readonly int[] Faces =
    {
        0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
        2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5
    };

    private GraphLoader graph;
    private Transform shell;
    private Material material;
    private Mesh nodeMesh, routeMesh;
    private Color[] nodeColors, routeColors;
    private Color[] nodeBase, routeBase;
    private float[] nodePhase, routePhase;
    private Vector3 centre;
    private bool built, building;
    private float visible;      // 0..1 fade
    private float opening;      // 0 = closed shell, 1 = opened outwards (exit)
    private float clock;
    private float eased;        // smoothed Progress

    private void Update()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (!built && !building && Wanted && graph != null && graph.IsLoaded && Camera.main != null) Build();
        if (!built) return;

        bool reduce = Comfort.ReduceMotion;
        float dt = Time.unscaledDeltaTime;
        if (Wanted)
        {
            visible = Mathf.MoveTowards(visible, 1f, dt / Mathf.Max(0.05f, fadeInSeconds));
            opening = 0f;
        }
        else
        {
            if (visible <= 0f) { if (shell.gameObject.activeSelf) shell.gameObject.SetActive(false); return; }
            float step = dt / Mathf.Max(0.05f, fadeOutSeconds);
            visible = Mathf.MoveTowards(visible, 0f, step);
            opening = Mathf.MoveTowards(opening, 1f, step);
        }
        if (visible > 0f && !shell.gameObject.activeSelf) shell.gameObject.SetActive(true);

        eased = Mathf.MoveTowards(eased, Mathf.Clamp01(Progress), dt * 0.5f);
        clock += dt * (reduce ? 0.25f : 1f);

        // Grows in from 85 % and opens outwards on the way out; turns slowly (still with reduced motion, but slower).
        float grow = Mathf.SmoothStep(0.85f, 1f, visible);
        float scale = grow * (1f + 0.45f * opening * opening);
        shell.localScale = Vector3.one * scale;
        if (Camera.main != null) shell.position = Camera.main.transform.position; // stays around the head
        shell.rotation = Quaternion.Euler(0f, clock * spin * (reduce ? 0.3f : 1f), 0f);
        material.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, visible));

        Animate(reduce);
    }

    private void Animate(bool reduce)
    {
        float glow = 0.55f + 0.45f * eased;
        // Airports: a slow twinkle, brighter the more is ready.
        int n = Corners.Length;
        for (int i = 0; i < nodeBase.Length; i++)
        {
            float tw = reduce ? 0.8f : 0.65f + 0.35f * Mathf.Sin(clock * 1.4f + nodePhase[i]);
            Color c = nodeBase[i];
            c.a = Mathf.Clamp01(tw * glow);
            for (int v = 0; v < n; v++) nodeColors[i * n + v] = c;
        }
        nodeMesh.colors = nodeColors;

        // Routes: a faint line with a light running along it; faster and more of them as the loading advances.
        float speed = reduce ? 0.1f : 0.25f + 0.9f * eased;
        for (int r = 0; r < routePhase.Length; r++)
        {
            float head = Mathf.Repeat(clock * speed + routePhase[r], 1.4f) - 0.2f;
            for (int j = 0; j < ArcSamples; j++)
            {
                float f = (float)j / (ArcSamples - 1);
                float d = (f - head) / 0.12f;
                float pulse = Mathf.Exp(-d * d);
                Color c = routeBase[r];
                c.a = Mathf.Clamp01((0.12f + 0.88f * pulse) * glow);
                routeColors[r * ArcSamples + j] = c;
            }
        }
        routeMesh.colors = routeColors;
    }

    /// <summary>Builds the shell once from the loaded graph, around where the user's head is now.</summary>
    private void Build()
    {
        building = true;
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        if (nodes.Count == 0) { building = false; return; }

        // Centre of the layout, and each airport's direction and relative distance from it.
        var bounds = new Bounds(nodes[0].transform.position, Vector3.zero);
        foreach (GraphNode nd in nodes) bounds.Encapsulate(nd.transform.position);
        Vector3 layoutCentre = bounds.center;
        float maxD = 0.01f;
        foreach (GraphNode nd in nodes) maxD = Mathf.Max(maxD, (nd.transform.position - layoutCentre).magnitude);

        Transform head = Camera.main.transform;
        centre = head.position;
        // Placed in front of the viewer's heading only for the first look: the shell is closed around them.
        shell = new GameObject("Start Ambience").transform;
        shell.position = centre;
        material = new Material(Shader.Find("Sprites/Default")) { name = "Start Ambience (runtime)" };

        int count = nodes.Count;
        var local = new Vector3[count];
        var index = new Dictionary<GraphNode, int>();
        var verts = new Vector3[count * Corners.Length];
        nodeBase = new Color[count];
        nodeColors = new Color[verts.Length];
        nodePhase = new float[count];
        var tris = new List<int>(count * Faces.Length);
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = nodes[i].transform.position - layoutCentre;
            Vector3 dir = offset.sqrMagnitude > 1e-6f ? offset.normalized : Vector3.up;
            float radius = Mathf.Lerp(radiusRange.x, radiusRange.y, offset.magnitude / maxD);
            local[i] = dir * radius;
            index[nodes[i]] = i;
            nodeBase[i] = nodes[i].baseColor;
            // Busy airports are bigger; distance scaling keeps the far ones from vanishing.
            float busy = Mathf.Clamp(nodes[i].transform.lossyScale.x / Mathf.Max(1e-4f, nodes[0].transform.lossyScale.x), 0.6f, 3f);
            float size = airportSize * (radius / radiusRange.x) * Mathf.Sqrt(busy);
            for (int v = 0; v < Corners.Length; v++) verts[i * Corners.Length + v] = local[i] + Corners[v] * size;
            foreach (int f in Faces) tris.Add(i * Corners.Length + f);
            nodePhase[i] = Random.value * Mathf.PI * 2f;
        }
        nodeMesh = NewMesh("Airports", verts, tris, MeshTopology.Triangles);
        AddPart("Airports", nodeMesh);

        // Routes: the busiest, as arcs that bulge outwards along the shell (never through the user).
        var edges = new List<GraphEdge>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (graph.Nodes.TryGetValue(e.sourceId, out GraphNode s) && graph.Nodes.TryGetValue(e.targetId, out GraphNode t) &&
                index.ContainsKey(s) && index.ContainsKey(t)) edges.Add(e);
        }
        edges.Sort((a, b) => b.weight.CompareTo(a.weight));
        if (edges.Count > routeCount) edges.RemoveRange(routeCount, edges.Count - routeCount);

        var lineVerts = new Vector3[edges.Count * ArcSamples];
        routeBase = new Color[edges.Count];
        routeColors = new Color[lineVerts.Length];
        routePhase = new float[edges.Count];
        var lines = new List<int>(edges.Count * (ArcSamples - 1) * 2);
        for (int r = 0; r < edges.Count; r++)
        {
            Vector3 a = local[index[graph.Nodes[edges[r].sourceId]]];
            Vector3 b = local[index[graph.Nodes[edges[r].targetId]]];
            Vector3 sum = a + b;
            Vector3 up = sum.sqrMagnitude > 0.25f ? sum.normalized : Vector3.up;
            float peak = Mathf.Max(a.magnitude, b.magnitude) * 1.12f;
            Vector3 control = 2f * (up * peak) - 0.5f * sum; // quadratic Bezier that peaks at `peak`
            for (int j = 0; j < ArcSamples; j++)
            {
                float f = (float)j / (ArcSamples - 1);
                lineVerts[r * ArcSamples + j] = (1 - f) * (1 - f) * a + 2 * (1 - f) * f * control + f * f * b;
                if (j > 0)
                {
                    lines.Add(r * ArcSamples + j - 1);
                    lines.Add(r * ArcSamples + j);
                }
            }
            routeBase[r] = edges[r].baseColor;
            routePhase[r] = Random.value * 1.4f;
        }
        routeMesh = NewMesh("Routes", lineVerts, lines, MeshTopology.Lines);
        AddPart("Routes", routeMesh);

        shell.gameObject.SetActive(false);
        built = true;
        building = false;
    }

    private static Mesh NewMesh(string name, Vector3[] verts, List<int> indices, MeshTopology topology)
    {
        var mesh = new Mesh { name = "Start Ambience " + name };
        if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.vertices = verts;
        mesh.colors = new Color[verts.Length];
        mesh.SetIndices(indices, topology, 0);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20f); // vertices stay in place; colours change only
        return mesh;
    }

    private void AddPart(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(shell, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private void OnDestroy()
    {
        if (shell != null) Destroy(shell.gameObject);
        if (material != null) Destroy(material);
        if (nodeMesh != null) Destroy(nodeMesh);
        if (routeMesh != null) Destroy(routeMesh);
    }
}
