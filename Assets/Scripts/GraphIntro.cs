using UnityEngine;

/// <summary>
/// Opening animation: when the graph has loaded, the airports unfold from its centre
/// (growing outwards while turning a little), then the routes fade in, then the airport
/// codes appear. It animates only the airports' parent ("Nodes" under the GraphLoader),
/// so sizes and colours set by the graph meanwhile are kept.
/// Self-contained: it adds itself when the scene has a GraphLoader and changes no other
/// script. Replay: the component's context menu, or Play().
/// </summary>
public class GraphIntro : MonoBehaviour
{
    public GraphLoader graph;
    public bool playOnLoad = true;
    [Tooltip("Time (s) the airports take to unfold.")]
    public float unfoldSeconds = 2.2f;
    [Tooltip("Turn (deg) around the vertical while unfolding.")]
    public float turnDegrees = 40f;
    [Tooltip("Time (s) the routes take to fade in afterwards.")]
    public float routesSeconds = 0.8f;

    /// <summary>True while the animation runs.</summary>
    public bool Playing => phase != Phase.Done;

    private enum Phase { Waiting, Unfold, Routes, Done }
    private Phase phase = Phase.Waiting;
    private float phaseStart;
    private Transform nodes;
    private GameObject labels;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<GraphIntro>() != null) return;
        GraphLoader loader = FindFirstObjectByType<GraphLoader>();
        if (loader != null) loader.gameObject.AddComponent<GraphIntro>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (!playOnLoad) phase = Phase.Done;
    }

    /// <summary>Plays the animation (again).</summary>
    [ContextMenu("Play")]
    public void Play()
    {
        if (graph == null || !graph.IsLoaded) return;
        nodes = graph.transform.Find("Nodes");
        if (nodes == null)
        {
            phase = Phase.Done;
            return;
        }
        phase = Phase.Unfold;
        phaseStart = Time.time;
        graph.SetEdgeFade(0f);
        Apply(0f);
        UISounds.Play(UISounds.Open, graph.transform.position);
    }

    // LateUpdate: after the GraphLoader's loading coroutine, so the full graph is never drawn first.
    private void LateUpdate()
    {
        if (graph == null) return;
        if (phase == Phase.Waiting)
        {
            if (graph.IsLoaded) Play();
            return;
        }
        if (phase == Phase.Done) return;

        // The airport codes (HubLabels) are made a little later than the graph: keep them hidden until the end.
        if (labels == null)
        {
            labels = GameObject.Find("HubLabels");
            if (labels != null) labels.SetActive(false);
        }

        float t = Time.time - phaseStart;
        if (phase == Phase.Unfold)
        {
            float s = Mathf.Clamp01(t / unfoldSeconds);
            Apply(1f - Mathf.Pow(1f - s, 3f)); // ease out: fast start, gentle landing
            if (s >= 1f)
            {
                phase = Phase.Routes;
                phaseStart = Time.time;
            }
        }
        else
        {
            float s = Mathf.Clamp01(t / routesSeconds);
            graph.SetEdgeFade(s);
            if (s >= 1f)
            {
                phase = Phase.Done;
                if (labels != null) labels.SetActive(true);
                labels = null;
            }
        }
    }

    private void OnDisable()
    {
        // Never leave the graph half unfolded.
        if (phase == Phase.Unfold || phase == Phase.Routes)
        {
            Apply(1f);
            if (graph != null) graph.SetEdgeFade(1f);
            if (labels != null) labels.SetActive(true);
            phase = Phase.Done;
        }
    }

    /// <summary>0 = folded into the centre, 1 = as the graph laid it out.</summary>
    private void Apply(float amount)
    {
        if (nodes == null) return;
        nodes.localScale = Vector3.one * Mathf.Max(0.001f, amount);
        nodes.localRotation = Quaternion.Euler(0f, (1f - amount) * -turnDegrees, 0f);
    }
}
