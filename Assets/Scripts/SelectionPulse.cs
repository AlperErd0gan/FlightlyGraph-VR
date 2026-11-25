using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Selection pulse (B16): the selected airport breathes gently (a soft halo around it) and its
/// connections light up one after another, nearest first, instead of all at once. Ends with
/// the normal highlight; any other selection, a cleared one or a restyle (timeline period,
/// layout, filter) ends it. With Reduce motion on, the halo stays still and the routes light
/// up together (the normal highlight).
/// Self-contained: adds itself when the scene has a GraphSelector, changes no other script.
/// </summary>
public class SelectionPulse : MonoBehaviour
{
    [Header("Halo")]
    public bool showHalo = true;
    [Tooltip("Halo size as a multiple of the airport's size at the dimmest / brightest point of a breath.")]
    public Vector2 haloScale = new Vector2(1.4f, 1.9f);
    [Tooltip("Halo opacity at the dimmest / brightest point of a breath.")]
    public Vector2 haloAlpha = new Vector2(0.08f, 0.26f);
    [Tooltip("Breaths per second (slow: ~0.5).")]
    public float breathHz = 0.5f;
    [Tooltip("Seconds the halo takes to fade in.")]
    public float haloFadeIn = 0.35f;

    [Header("Connections")]
    public bool lightRoutes = true;
    [Tooltip("The whole wave lasts at most this long (s), however many routes the airport has.")]
    public float waveSeconds = 1.1f;
    [Tooltip("Seconds one route takes to grow to its highlighted width.")]
    public float growSeconds = 0.3f;

    private GraphSelector selector;
    private GraphLoader graph;
    private GraphNode node;
    private Transform halo;
    private Material haloMaterial;
    private float startedAt;

    private class Leg
    {
        public GraphEdge edge;
        public float delay;
    }
    private readonly List<Leg> legs = new List<Leg>();
    private bool waveActive;
    private bool subscribed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<SelectionPulse>() != null) return;
        if (FindFirstObjectByType<GraphSelector>() == null) return;
        new GameObject("SelectionPulse").AddComponent<SelectionPulse>();
    }

    private void OnEnable() => TrySubscribe();

    private void OnDisable()
    {
        Unsubscribe();
        Stop();
    }

    private void OnDestroy()
    {
        if (haloMaterial != null) Destroy(haloMaterial);
    }

    private void TrySubscribe()
    {
        if (subscribed) return;
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (selector == null) return;
        graph = selector.graph != null ? selector.graph : FindFirstObjectByType<GraphLoader>();
        if (graph == null) return;
        subscribed = true;
        selector.NodeSelected += OnNode;
        selector.EdgeSelected += OnOther;
        selector.PathSelected += OnPath;
        selector.SelectionCleared += Stop;
        graph.PeriodChanged += OnRestyle;
        graph.LayoutChanged += OnRestyle;
        graph.FilterChanged += OnRestyle;
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        subscribed = false;
        selector.NodeSelected -= OnNode;
        selector.EdgeSelected -= OnOther;
        selector.PathSelected -= OnPath;
        selector.SelectionCleared -= Stop;
        graph.PeriodChanged -= OnRestyle;
        graph.LayoutChanged -= OnRestyle;
        graph.FilterChanged -= OnRestyle;
    }

    private void OnOther(GraphEdge e) => Stop();
    private void OnPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges) => Stop();

    /// <summary>Selector redraws its highlight itself; the wave stops writing (the halo goes on).</summary>
    private void OnRestyle() => waveActive = false;

    private void OnNode(GraphNode n)
    {
        Stop();
        if (n == null || graph == null) return;
        node = n;
        startedAt = Time.unscaledTime;
        if (showHalo) BuildHalo();
        if (lightRoutes && !Comfort.ReduceMotion) BeginWave();
    }

    private void Stop()
    {
        node = null;
        waveActive = false;
        legs.Clear();
        if (halo != null) halo.gameObject.SetActive(false);
    }

    // ---- Halo ----------------------------------------------------------------

    private void BuildHalo()
    {
        if (halo == null)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Selection Halo";
            Destroy(sphere.GetComponent<Collider>());
            Renderer r = sphere.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Shader shader = Shader.Find("Sprites/Default");
            haloMaterial = new Material(shader);
            r.sharedMaterial = haloMaterial;
            halo = sphere.transform;
        }
        // Child of the airport: it follows it and scales with it (map, layouts, timeline).
        halo.SetParent(node.transform, false);
        halo.localPosition = Vector3.zero;
        halo.localRotation = Quaternion.identity;
        halo.gameObject.SetActive(true);
        ApplyHalo(0f);
    }

    private void ApplyHalo(float t)
    {
        if (halo == null || node == null) return;
        float fade = Mathf.SmoothStep(0f, 1f, (Time.unscaledTime - startedAt) / Mathf.Max(0.01f, haloFadeIn));
        float breath = Comfort.ReduceMotion ? 0.5f : 0.5f - 0.5f * Mathf.Cos(t * breathHz * Mathf.PI * 2f);
        float eased = Mathf.SmoothStep(0f, 1f, breath);
        float scale = Mathf.Lerp(haloScale.x, haloScale.y, eased);
        halo.localScale = Vector3.one * scale;
        Color c = node.baseColor;
        c.a = Mathf.Lerp(haloAlpha.x, haloAlpha.y, eased) * fade;
        haloMaterial.color = c;
    }

    // ---- Wave of connections ---------------------------------------------------

    private void BeginWave()
    {
        legs.Clear();
        IReadOnlyList<GraphEdge> edges = graph.EdgesOf(node.id);
        if (edges == null || edges.Count == 0) return;

        Vector3 centre = node.transform.position;
        var order = new List<KeyValuePair<float, GraphEdge>>(edges.Count);
        foreach (GraphEdge e in edges)
        {
            string otherId = e.sourceId == node.id ? e.targetId : e.sourceId;
            if (!graph.Nodes.TryGetValue(otherId, out GraphNode other)) continue;
            order.Add(new KeyValuePair<float, GraphEdge>(Vector3.SqrMagnitude(other.transform.position - centre), e));
        }
        order.Sort((a, b) => a.Key.CompareTo(b.Key));

        int n = order.Count;
        float span = Mathf.Max(0f, waveSeconds - growSeconds);
        for (int i = 0; i < n; i++)
        {
            legs.Add(new Leg { edge = order[i].Value, delay = n > 1 ? span * i / (n - 1) : 0f });
        }
        waveActive = legs.Count > 0;
        // Start from the resting look: the selector has just drawn them highlighted.
        if (waveActive) WriteWave(0f);
    }

    private void WriteWave(float t)
    {
        Color lit = selector.edgeHighlightColor;
        float fullWidth = graph.edgeWidth * selector.highlightedEdgeWidthMultiplier;
        bool done = true;
        foreach (Leg leg in legs)
        {
            float k = Mathf.Clamp01((t - leg.delay) / Mathf.Max(0.01f, growSeconds));
            if (k < 1f) done = false;
            float e = Mathf.SmoothStep(0f, 1f, k);
            Color rest = leg.edge.baseColor;
            rest.a = Mathf.Min(rest.a, selector.dimmedEdgeAlpha);
            Color c = Color.Lerp(rest, lit, e);
            graph.SetEdgeDisplay(leg.edge, c, Mathf.Lerp(graph.edgeWidth, fullWidth, e));
        }
        if (done) waveActive = false; // ended on the exact highlighted look
    }

    private void Update()
    {
        if (!subscribed) TrySubscribe();
        if (node == null) return;
        float t = Time.unscaledTime - startedAt;
        ApplyHalo(t);
        if (waveActive) WriteWave(t);
    }
}
