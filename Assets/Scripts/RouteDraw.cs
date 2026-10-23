using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws a found route (ConnectionFinder, "route from A to B") leg by leg: a light travels
/// from A along each arc to the next stop, the line grows behind it, and each airport it
/// reaches flashes. The route's own highlighted arcs are hidden while it plays and shown
/// again at the end, so afterwards the route looks as before (GraphSelector's highlight).
/// A new selection, or clearing it, stops the animation.
/// Self-contained: it adds itself when the scene has a GraphSelector and changes no other script.
/// </summary>
public class RouteDraw : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Travel speed of the light along the arcs (m/s).")]
    public float speed = 4f;
    [Tooltip("Shortest and longest time (s) one leg takes.")]
    public float minLegSeconds = 0.4f;
    public float maxLegSeconds = 1.4f;
    [Tooltip("Pause (s) at each stop in between.")]
    public float stopPause = 0.25f;
    [Tooltip("Line width relative to the highlighted route's width.")]
    public float widthScale = 1.3f;
    [Tooltip("Thinnest line (m).")]
    public float minWidth = 0.008f;
    [Tooltip("Flash at each airport: size relative to the airport, and duration (s).")]
    public float flashScale = 3f;
    public float flashSeconds = 0.5f;

    private Coroutine running;
    private Transform root;
    private Material lineMaterial;
    private readonly List<Material> materials = new List<Material>();
    private readonly List<(GraphEdge edge, Color color, float width)> hidden = new List<(GraphEdge, Color, float)>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<RouteDraw>() != null) return;
        GraphSelector s = FindFirstObjectByType<GraphSelector>();
        if (s != null) s.gameObject.AddComponent<RouteDraw>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (selector == null || graph == null)
        {
            enabled = false;
            return;
        }
        selector.PathSelected += OnPath;
        selector.NodeSelected += OnOtherSelection;
        selector.EdgeSelected += OnOtherSelection;
        selector.SelectionCleared += Cancel;
    }

    private void OnDestroy()
    {
        if (selector != null)
        {
            selector.PathSelected -= OnPath;
            selector.NodeSelected -= OnOtherSelection;
            selector.EdgeSelected -= OnOtherSelection;
            selector.SelectionCleared -= Cancel;
        }
        Cancel();
        foreach (Material m in materials) if (m != null) Destroy(m);
    }

    private void OnOtherSelection(GraphNode node) => Cancel();

    private void OnOtherSelection(GraphEdge edge) => Cancel();

    private void OnPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        Cancel();
        if (nodes == null || edges == null || edges.Count == 0 || nodes.Count != edges.Count + 1) return;
        running = StartCoroutine(Play(new List<GraphNode>(nodes), new List<GraphEdge>(edges)));
    }

    /// <summary>Stops the animation and removes its lines; the arcs are left as the selector set them.</summary>
    private void Cancel()
    {
        if (running != null) StopCoroutine(running);
        running = null;
        hidden.Clear();
        if (root != null) Destroy(root.gameObject);
        root = null;
    }

    private IEnumerator Play(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        root = new GameObject("RouteDraw").transform;

        // Hide the route's highlighted arcs (the selector has just set them); drawn again at the end.
        foreach (GraphEdge e in edges)
        {
            hidden.Add((e, e.displayColor, e.displayWidth));
            graph.SetEdgeDisplay(e, e.displayColor, 0f);
        }
        Color color = hidden[0].color;
        color.a = 1f;
        float width = Mathf.Max(minWidth, hidden[0].width * widthScale);

        float nodeSize = nodes[0].transform.lossyScale.x;
        Transform head = MakeSphere("RouteDraw Light", Color.white, out Material headMaterial).transform;
        head.SetParent(root, true);
        head.localScale = Vector3.one * Mathf.Max(width * 3f, nodeSize * 0.5f);

        StartCoroutine(Flash(nodes[0], color));
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].points == null || edges[i].points.Length < 2) continue;
            Vector3[] path = Oriented(edges[i], nodes[i]);
            LineRenderer line = MakeLine("Leg " + (i + 1), color, width);
            float length = Length(path);
            float seconds = Mathf.Clamp(length / Mathf.Max(0.01f, speed), minLegSeconds, maxLegSeconds);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float s = t / seconds;
                s = s * s * (3f - 2f * s); // smoothstep: gentle start and arrival
                head.position = Partial(path, length * s, line);
                yield return null;
            }
            head.position = Partial(path, length, line);
            UISounds.Play(UISounds.Hover, nodes[i + 1].transform.position);
            StartCoroutine(Flash(nodes[i + 1], color));
            if (i < edges.Count - 1) yield return new WaitForSeconds(stopPause);
        }
        UISounds.Play(UISounds.Click, nodes[nodes.Count - 1].transform.position);
        Destroy(head.gameObject);
        Release(headMaterial);
        yield return new WaitForSeconds(flashSeconds);

        // Back to the selector's highlight (unless something else changed an arc meanwhile).
        foreach ((GraphEdge edge, Color c, float w) in hidden)
        {
            if (edge.displayWidth == 0f) graph.SetEdgeDisplay(edge, c, w);
        }
        hidden.Clear();
        Destroy(root.gameObject);
        root = null;
        running = null;
    }

    /// <summary>The edge's arc from `from` towards the other end (edges are stored in one direction only).</summary>
    private static Vector3[] Oriented(GraphEdge edge, GraphNode from)
    {
        var points = (Vector3[])edge.points.Clone();
        Vector3 at = from.transform.position;
        if ((points[points.Length - 1] - at).sqrMagnitude < (points[0] - at).sqrMagnitude) System.Array.Reverse(points);
        return points;
    }

    private static float Length(Vector3[] points)
    {
        float sum = 0f;
        for (int i = 1; i < points.Length; i++) sum += Vector3.Distance(points[i - 1], points[i]);
        return sum;
    }

    /// <summary>Sets the line to the first `distance` metres of the arc; returns its tip.</summary>
    private static Vector3 Partial(Vector3[] points, float distance, LineRenderer line)
    {
        var shown = new List<Vector3> { points[0] };
        float left = distance;
        for (int i = 1; i < points.Length; i++)
        {
            float step = Vector3.Distance(points[i - 1], points[i]);
            if (step >= left)
            {
                shown.Add(step > 1e-6f ? Vector3.Lerp(points[i - 1], points[i], left / step) : points[i]);
                break;
            }
            shown.Add(points[i]);
            left -= step;
        }
        line.positionCount = shown.Count;
        line.SetPositions(shown.ToArray());
        return shown[shown.Count - 1];
    }

    private IEnumerator Flash(GraphNode node, Color color)
    {
        GameObject flash = MakeSphere("RouteDraw Flash", color, out Material material);
        flash.transform.SetParent(root, true);
        Vector3 at = node.transform.position;
        float size = node.transform.lossyScale.x;
        for (float t = 0f; t < flashSeconds && flash != null; t += Time.deltaTime)
        {
            float s = t / flashSeconds;
            flash.transform.position = at;
            flash.transform.localScale = Vector3.one * size * Mathf.Lerp(1.2f, flashScale, 1f - (1f - s) * (1f - s));
            Color c = color;
            c.a = 0.7f * (1f - s);
            material.color = c;
            yield return null;
        }
        if (flash != null) Destroy(flash);
        Release(material);
    }

    private void Release(Material material)
    {
        materials.Remove(material);
        Destroy(material);
    }

    private LineRenderer MakeLine(string name, Color color, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 0;
        line.widthMultiplier = width;
        line.numCapVertices = 4;
        line.startColor = line.endColor = color;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (lineMaterial == null)
        {
            lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "RouteDraw Line (runtime)" };
            materials.Add(lineMaterial);
        }
        line.sharedMaterial = lineMaterial;
        return line;
    }

    private GameObject MakeSphere(string name, Color color, out Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>()); // must never block XR rays or selection
        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        material = new Material(Shader.Find("Sprites/Default")) { name = name + " (runtime)", color = color };
        materials.Add(material);
        r.sharedMaterial = material;
        return go;
    }
}
