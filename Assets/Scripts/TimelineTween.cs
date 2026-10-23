using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Smooth timeline: when the month changes (GraphLoader.PeriodChanged), airports grow or
/// shrink to their new size over `seconds` instead of jumping, routes that appear grow in
/// from nothing and routes that disappear thin out before they go. The graph has already
/// set the new month's look when this starts; this only animates from what was shown
/// before to it. Other changes (filters, view mode, map) are not animated.
/// Self-contained: it adds itself when the scene has a GraphLoader and changes no other script.
/// </summary>
public class TimelineTween : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Length of the change (s); keep below the timeline's play step.")]
    public float seconds = 0.45f;
    [Tooltip("Animate the routes that appear / disappear too.")]
    public bool animateRoutes = true;

    private struct EdgeFade
    {
        public GraphEdge edge;
        public float from;
        public float to;
        public bool leaving;
    }

    private bool hooked;
    private float start = -1f;
    // What each airport looked like last frame, and the size the graph set for it.
    private readonly Dictionary<GraphNode, Vector3> shown = new Dictionary<GraphNode, Vector3>();
    private readonly Dictionary<GraphNode, Vector3> fromScale = new Dictionary<GraphNode, Vector3>();
    private readonly Dictionary<GraphNode, Vector3> toScale = new Dictionary<GraphNode, Vector3>();
    // Routes the graph shows in the current month (before any fade-out of ours).
    private readonly HashSet<GraphEdge> visibleNow = new HashSet<GraphEdge>();
    private readonly List<EdgeFade> fades = new List<EdgeFade>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<TimelineTween>() != null) return;
        GraphLoader loader = FindFirstObjectByType<GraphLoader>();
        if (loader != null) loader.gameObject.AddComponent<TimelineTween>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
    }

    private void OnDestroy()
    {
        if (!hooked || graph == null) return;
        graph.PeriodChanged -= OnPeriodChanged;
        graph.FilterChanged -= RememberVisible;
        graph.LayoutChanged -= RememberVisible;
    }

    private void LateUpdate()
    {
        if (graph == null || !graph.IsLoaded) return;
        if (!hooked)
        {
            hooked = true;
            graph.PeriodChanged += OnPeriodChanged;
            graph.FilterChanged += RememberVisible;
            graph.LayoutChanged += RememberVisible;
            RememberVisible();
        }

        if (start < 0f)
        {
            foreach (GraphNode node in graph.Nodes.Values) shown[node] = node.transform.localScale;
            return;
        }

        float s = Mathf.Clamp01((Time.time - start) / seconds);
        float k = s * s * (3f - 2f * s); // smoothstep
        foreach (KeyValuePair<GraphNode, Vector3> to in toScale)
        {
            Vector3 size = Vector3.LerpUnclamped(fromScale[to.Key], to.Value, k);
            to.Key.transform.localScale = size;
            shown[to.Key] = size;
        }
        foreach (EdgeFade f in fades)
        {
            graph.SetEdgeDisplay(f.edge, f.edge.displayColor, Mathf.Lerp(f.from, f.to, k));
        }
        if (s >= 1f) Finish();
    }

    private void OnPeriodChanged()
    {
        if (start >= 0f) Finish(); // a new month before the last change ended: start from what is shown
        fromScale.Clear();
        toScale.Clear();
        foreach (GraphNode node in graph.Nodes.Values)
        {
            Vector3 target = node.transform.localScale; // already the new month's size
            Vector3 before = shown.TryGetValue(node, out Vector3 v) ? v : target;
            if ((before - target).sqrMagnitude < 1e-10f) continue;
            fromScale[node] = before;
            toScale[node] = target;
            node.transform.localScale = before;
        }

        fades.Clear();
        if (animateRoutes)
        {
            var now = new HashSet<GraphEdge>();
            foreach (GraphEdge e in graph.Edges) if (e.visible) now.Add(e);
            foreach (GraphEdge e in now)
            {
                if (visibleNow.Contains(e)) continue;
                fades.Add(new EdgeFade { edge = e, from = 0f, to = e.displayWidth });
                graph.SetEdgeDisplay(e, e.displayColor, 0f);
            }
            foreach (GraphEdge e in visibleNow)
            {
                if (now.Contains(e)) continue;
                // Keep it drawn while it thins out; hidden again in Finish.
                e.visible = true;
                fades.Add(new EdgeFade { edge = e, from = e.displayWidth, to = 0f, leaving = true });
            }
            visibleNow.Clear();
            visibleNow.UnionWith(now);
        }
        start = Time.time;
    }

    private void Finish()
    {
        foreach (KeyValuePair<GraphNode, Vector3> to in toScale)
        {
            to.Key.transform.localScale = to.Value;
            shown[to.Key] = to.Value;
        }
        foreach (EdgeFade f in fades)
        {
            if (f.leaving)
            {
                // Hidden again only if the graph still hides it (a later change may have brought it back).
                if (!visibleNow.Contains(f.edge)) f.edge.visible = false;
                graph.SetEdgeDisplay(f.edge, f.edge.displayColor, f.from);
            }
            else
            {
                graph.SetEdgeDisplay(f.edge, f.edge.displayColor, f.to);
            }
        }
        toScale.Clear();
        fromScale.Clear();
        fades.Clear();
        start = -1f;
    }

    /// <summary>Takes the routes shown now as the starting point, without animating (filters, view, map).</summary>
    private void RememberVisible()
    {
        if (start >= 0f) Finish();
        visibleNow.Clear();
        foreach (GraphEdge e in graph.Edges) if (e.visible) visibleNow.Add(e);
    }
}
