using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Speaks a short description of the selected node, edge or route through the
/// Meta Voice SDK's TTSSpeaker (Wit.ai text-to-speech). The sentences are built
/// from the data (templates, no language model); the speaker moves next to the
/// selection so the voice comes from it. A new selection interrupts, clearing the
/// selection stops.
///
/// The TTSSpeaker is called by reflection (Speak(string), Stop()) so this script
/// compiles with any Voice SDK version (its namespace moved from Facebook.WitAi to
/// Meta.WitAi) and even without the SDK installed. Voice, speed and the disk cache
/// are set on the TTSSpeaker / TTSWitService objects in the Inspector.
/// </summary>
public class NodeNarrator : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("The TTSSpeaker component (Voice SDK). Found in the scene by type name if unset.")]
    public Component speaker;

    [Header("When to speak")]
    public bool narrationEnabled = true;
    public bool speakNodes = true;
    public bool speakEdges = true;
    public bool speakRoutes = true;

    [Header("Sound")]
    [Tooltip("Move the speaker to the selection so the voice comes from it (3D sound).")]
    public bool speakFromSelection = true;
    [Tooltip("Height above the selected node for the speaker (m).")]
    public float speakerOffset = 0.2f;
    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Up to this distance (m) the voice is not quieter (AudioSource Min Distance). Nodes are 3-8 m away in the immersive view.")]
    public float audibleDistance = 10f;
    [Tooltip("The same sentence asked again within this many seconds is ignored instead of restarting it.")]
    public float repeatGuardSeconds = 4f;
    [Tooltip("Write every sentence to the Console before speaking it (to check narration is triggered).")]
    public bool logSentences = true;

    public enum Detail { Short, Full }

    [Header("Words")]
    [Tooltip("Short: name, traffic and busiest link, short airport names. Full: also delays, cargo, operator, cluster.")]
    public Detail detail = Detail.Short;
    [Tooltip("What node.value / edge.weight count, as spoken (e.g. \"flights\", \"routes\").")]
    public string unit = "flights";
    [Tooltip("Spoken after the flight count, e.g. \"between 2020 and 2025\". Empty to leave out.")]
    public string periodPhrase = "between 2020 and 2025";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private MethodInfo speakMethod;
    private MethodInfo stopMethod;
    private string lastText;
    private float lastTime = -999f;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        // Dragging a GameObject into the field assigns its first component (the Transform):
        // look for the TTSSpeaker on that object and its children, then in the scene.
        if (speaker != null && speaker.GetType().Name != "TTSSpeaker")
        {
            speaker = FindOnObject(speaker.gameObject, "TTSSpeaker");
        }
        if (speaker == null) speaker = FindComponentByTypeName("TTSSpeaker");
        if (speaker == null)
        {
            Debug.LogError("NodeNarrator: no TTSSpeaker in the scene (add the Voice SDK TTS setup, or drag it into Speaker); narration disabled.");
            return;
        }
        System.Type type = speaker.GetType();
        speakMethod = type.GetMethod("Speak", new[] { typeof(string) });
        stopMethod = type.GetMethod("Stop", System.Type.EmptyTypes);
        if (speakMethod == null)
        {
            Debug.LogError($"NodeNarrator: {type.FullName} has no Speak(string); narration disabled.");
        }

        AudioSource source = speaker.GetComponentInChildren<AudioSource>();
        if (source != null)
        {
            source.volume = volume;
            if (speakFromSelection) source.spatialBlend = 1f; // 3D: the voice comes from the speaker's position
            // Default Min Distance (1 m) makes a node 4 m away speak at about a quarter of the volume.
            source.minDistance = audibleDistance;
            source.maxDistance = Mathf.Max(source.maxDistance, audibleDistance * 10f);
        }
    }

    private void Start()
    {
        AudioListener listener = FindFirstObjectByType<AudioListener>();
        Debug.Log($"NodeNarrator: ready = {speakMethod != null}, speaker = {(speaker != null ? speaker.GetType().FullName + " on '" + speaker.name + "'" : "none")}, " +
                  $"selector = {(selector != null ? selector.name : "none")}, audio listener = {(listener != null ? listener.name : "NONE")}");
    }

    private void OnEnable()
    {
        if (selector == null) return;
        selector.NodeSelected += OnNode;
        selector.EdgeSelected += OnEdge;
        selector.PathSelected += OnPath;
        selector.SelectionCleared += StopAndForget;
    }

    private void OnDisable()
    {
        if (selector == null) return;
        selector.NodeSelected -= OnNode;
        selector.EdgeSelected -= OnEdge;
        selector.PathSelected -= OnPath;
        selector.SelectionCleared -= StopAndForget;
    }

    private void OnNode(GraphNode node)
    {
        if (speakNodes) Say(DescribeNode(node), node.transform.position);
    }

    private void OnEdge(GraphEdge edge)
    {
        if (speakEdges) Say(DescribeEdge(edge), edge.Midpoint);
    }

    private void OnPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        if (speakRoutes) Say(DescribeRoute(nodes, edges), nodes[nodes.Count - 1].transform.position);
    }

    private void Say(string text, Vector3 position)
    {
        if (!narrationEnabled || speakMethod == null || string.IsNullOrEmpty(text)) return;
        // The same selection event can arrive twice (e.g. select enter + exit): restarting
        // the sentence would sound like it was cut, so let it finish.
        if (text == lastText && Time.time - lastTime < repeatGuardSeconds) return;
        lastText = text;
        lastTime = Time.time;
        if (logSentences)
        {
            AudioListener listener = FindFirstObjectByType<AudioListener>();
            Debug.Log($"NodeNarrator: speaking \"{text}\" (listener: {(listener != null ? listener.name : "NONE")}, " +
                      $"distance {(listener != null ? Vector3.Distance(listener.transform.position, position).ToString("F1") : "?")} m)");
        }
        Stop();
        if (speakFromSelection) speaker.transform.position = position + Vector3.up * speakerOffset;
        speakMethod.Invoke(speaker, new object[] { text });
    }

    public void Stop()
    {
        if (stopMethod != null) stopMethod.Invoke(speaker, null);
    }

    private void StopAndForget()
    {
        Stop();
        lastText = null;
    }

    // ---- Sentences -------------------------------------------------------

    public string DescribeNode(GraphNode node)
    {
        NodeData d = node.data;
        if (detail == Detail.Short)
        {
            var sbShort = new StringBuilder();
            sbShort.Append(ShortName(node));
            if (d != null && !string.IsNullOrEmpty(d.country)) sbShort.Append(", ").Append(d.country);
            sbShort.Append(". ").Append(Amount(node.value)).Append(' ').Append(unit).Append('.');
            GraphNode partner = BusiestPartner(node);
            if (partner != null) sbShort.Append(" Busiest link: ").Append(ShortName(partner)).Append('.');
            return sbShort.ToString();
        }

        var sb = new StringBuilder();
        sb.Append(SpokenName(node));
        if (d != null && !string.IsNullOrEmpty(d.city))
        {
            sb.Append(", ").Append(d.city);
            if (!string.IsNullOrEmpty(d.country)) sb.Append(", ").Append(d.country);
        }
        sb.Append(". ");

        sb.Append(Amount(node.value)).Append(' ').Append(unit);
        if (!string.IsNullOrEmpty(periodPhrase)) sb.Append(' ').Append(periodPhrase);
        sb.Append(". ");

        if (d != null && d.departures + d.arrivals > 0)
        {
            if (d.avgDepDelayMin.HasValue) sb.Append("Departures are on average ").Append(Delay(d.avgDepDelayMin.Value)).Append(". ");
            int cargo = Mathf.RoundToInt(d.cargoShare * 100f);
            if (cargo >= 1) sb.Append("About ").Append(cargo).Append(" percent cargo. ");
            if (!string.IsNullOrEmpty(d.topOperator)) sb.Append("Top operator: ").Append(Spell(d.topOperator)).Append(". ");
        }

        GraphNode other = BusiestPartner(node);
        if (other != null) sb.Append("Busiest link: ").Append(SpokenName(other)).Append(". ");

        if (graph.colorByCommunity)
        {
            sb.Append("Part of the cluster around ").Append(SpokenList(graph.CommunityMembers(node.community), 3)).Append('.');
        }
        return sb.ToString().Trim();
    }

    public string DescribeEdge(GraphEdge edge)
    {
        GraphNode a = graph.Nodes[edge.sourceId];
        GraphNode b = graph.Nodes[edge.targetId];
        EdgeData d = edge.data;
        if (detail == Detail.Short)
        {
            return $"{ShortName(a)} to {ShortName(b)}. {Amount(edge.weight)} {unit}, " +
                   $"about {Amount(Mathf.RoundToInt(GreatCircleKm(a, b)))} kilometres.";
        }
        var sb = new StringBuilder();
        sb.Append(SpokenName(a)).Append(" to ").Append(SpokenName(b)).Append(". ");
        sb.Append(Amount(edge.weight)).Append(' ').Append(unit);
        if (!string.IsNullOrEmpty(periodPhrase)) sb.Append(' ').Append(periodPhrase);
        sb.Append(". ");
        sb.Append("Distance about ").Append(Amount(Mathf.RoundToInt(GreatCircleKm(a, b)))).Append(" kilometres. ");
        if (d != null)
        {
            if (d.avgDurationMin.HasValue) sb.Append("Average block time ").Append(Duration(d.avgDurationMin.Value)).Append(". ");
            string segment = TopKey(d.segments);
            if (segment != null) sb.Append("Mostly ").Append(segment.ToLowerInvariant()).Append(" flights.");
        }
        return sb.ToString().Trim();
    }

    public string DescribeRoute(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        GraphNode from = nodes[0];
        GraphNode to = nodes[nodes.Count - 1];
        if (detail == Detail.Short)
        {
            if (edges.Count == 1) return $"{ShortName(from)} to {ShortName(to)}: direct, {Amount(edges[0].weight)} {unit}.";
            var viaShort = new List<string>();
            for (int i = 1; i < nodes.Count - 1; i++) viaShort.Add(ShortName(nodes[i]));
            int stopCount = edges.Count - 1;
            return $"{ShortName(from)} to {ShortName(to)}: {stopCount} {(stopCount == 1 ? "stop" : "stops")}, via {string.Join(" and ", viaShort)}.";
        }
        var sb = new StringBuilder();
        sb.Append("From ").Append(SpokenName(from)).Append(" to ").Append(SpokenName(to)).Append(". ");
        if (edges.Count == 1)
        {
            sb.Append("There is a direct connection with ").Append(Amount(edges[0].weight)).Append(' ').Append(unit).Append('.');
        }
        else
        {
            int stops = edges.Count - 1;
            var via = new List<GraphNode>();
            for (int i = 1; i < nodes.Count - 1; i++) via.Add(nodes[i]);
            sb.Append("No direct connection. ").Append(stops).Append(stops == 1 ? " stop, via " : " stops, via ")
              .Append(SpokenList(via, via.Count)).Append('.');
        }
        return sb.ToString();
    }

    // ---- Wording helpers -------------------------------------------------

    private GraphNode BusiestPartner(GraphNode node)
    {
        GraphEdge busiest = null;
        foreach (GraphEdge e in graph.EdgesOf(node.id))
        {
            if (busiest == null || e.weight > busiest.weight) busiest = e;
        }
        return busiest == null ? null : graph.Nodes[busiest.sourceId == node.id ? busiest.targetId : busiest.sourceId];
    }

    private static readonly string[] NameNoise =
    {
        "International Airport", "International", "Airport", "Aeroporto", "Aeropuerto", "Aéroport", "Flughafen", "Havalimanı",
    };

    /// <summary>
    /// Short spoken name: "Amsterdam Airport Schiphol" -> "Amsterdam Schiphol",
    /// "Zürich Airport" -> "Zürich". Names still longer than three words
    /// ("Rome–Fiumicino Leonardo da Vinci ...") fall back to the city.
    /// </summary>
    private static string ShortName(GraphNode node)
    {
        string name = SpokenName(node);
        foreach (string noise in NameNoise)
        {
            name = name.Replace(noise, " ");
        }
        name = string.Join(" ", name.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
        string city = node.data != null ? node.data.city : null;
        if ((name.Length == 0 || name.Split(' ').Length > 3) && !string.IsNullOrEmpty(city)) return city;
        return name.Length > 0 ? name : SpokenName(node);
    }

    /// <summary>"İstanbul Airport (IST)" -> "İstanbul Airport".</summary>
    private static string SpokenName(GraphNode node)
    {
        string label = node.label ?? node.id;
        int paren = label.LastIndexOf(" (", System.StringComparison.Ordinal);
        return paren > 0 ? label.Substring(0, paren) : label;
    }

    private static string SpokenList(IReadOnlyList<GraphNode> nodes, int max)
    {
        var names = new List<string>();
        for (int i = 0; i < nodes.Count && i < max; i++) names.Add(SpokenName(nodes[i]));
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
    }

    /// <summary>Round numbers the way people say them: 2.2 million, 215 thousand, 830.</summary>
    private static string Amount(int value)
    {
        if (value >= 1000000) return (value / 1000000f).ToString("0.#", Inv) + " million";
        if (value >= 10000) return Mathf.RoundToInt(value / 1000f).ToString(Inv) + " thousand";
        return value.ToString("N0", Inv);
    }

    private static string Delay(float minutes)
    {
        int m = Mathf.RoundToInt(minutes);
        if (m == 0) return "on time";
        int abs = Mathf.Abs(m);
        return abs + (abs == 1 ? " minute " : " minutes ") + (m > 0 ? "late" : "early");
    }

    private static string Duration(float minutes)
    {
        int total = Mathf.RoundToInt(minutes);
        if (total < 60) return total + " minutes";
        int h = total / 60;
        int m = total % 60;
        return h + (h == 1 ? " hour" : " hours") + (m > 0 ? " " + m + " minutes" : "");
    }

    /// <summary>ICAO operator codes read letter by letter ("T H Y").</summary>
    private static string Spell(string code)
    {
        return string.Join(" ", code.ToCharArray());
    }

    private static string TopKey(Dictionary<string, int> counts)
    {
        if (counts == null) return null;
        string best = null;
        int bestCount = 0;
        foreach (KeyValuePair<string, int> kv in counts)
        {
            if (kv.Value > bestCount)
            {
                best = kv.Key;
                bestCount = kv.Value;
            }
        }
        return best;
    }

    private static float GreatCircleKm(GraphNode a, GraphNode b)
    {
        const float earthRadiusKm = 6371f;
        float p1 = a.lat * Mathf.Deg2Rad;
        float p2 = b.lat * Mathf.Deg2Rad;
        float dp = (b.lat - a.lat) * Mathf.Deg2Rad;
        float dl = (b.lon - a.lon) * Mathf.Deg2Rad;
        float h = Mathf.Sin(dp / 2f) * Mathf.Sin(dp / 2f) +
                  Mathf.Cos(p1) * Mathf.Cos(p2) * Mathf.Sin(dl / 2f) * Mathf.Sin(dl / 2f);
        return 2f * earthRadiusKm * Mathf.Asin(Mathf.Min(1f, Mathf.Sqrt(h)));
    }

    private static Component FindOnObject(GameObject go, string typeName)
    {
        foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour.GetType().Name == typeName) return behaviour;
        }
        return null;
    }

    private static Component FindComponentByTypeName(string typeName)
    {
        foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (behaviour.GetType().Name == typeName) return behaviour;
        }
        return null;
    }
}
