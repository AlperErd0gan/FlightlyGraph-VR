using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// "How do I get from A to B?": select a node (grip / pinch), point the same or
/// the other controller at a second node and press its primary button
/// (A on the right controller, X on the left; key 1 in the XR Interaction
/// Simulator; Shift+click on desktop). Shows the direct edge if there is one,
/// otherwise the route with the fewest stops (breadth-first search over all
/// edges, busiest legs tried first, so among equally short routes busier ones win).
/// While the live flights are shown (FlightPanel open) it shows a real connection
/// instead (TASKS B7): actual flights of that day, each next one leaving 45 min .. 6 h
/// after the previous one lands (ConnectionSearch), the first one leaving after the
/// panel's time; Earlier / Later step through the day's options, Watch plays it, and
/// "Same airline only" keeps to one operator. No connection that day: the network's
/// route is shown with a note.
/// What goes wrong (nothing selected, no airport pointed at, no route) is said on the voice
/// assistant's caption, not only in the Console; a found connection pulses the controller.
/// </summary>
public class ConnectionFinder : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;

    [Header("Real connections (live flights shown, TASKS B7)")]
    [Tooltip("Live flights panel: while it is open, connections use that day's real flights. Found in the scene if unset.")]
    public FlightPanel flights;
    [Tooltip("Shows the connection's card. Found in the scene if unset.")]
    public GraphInfoPanel infoPanel;
    [Tooltip("Shortest time between landing and the next flight leaving, at the same airport (minutes).")]
    public float minConnectionMinutes = 45f;
    [Tooltip("Longest wait for the next flight (hours).")]
    public float maxWaitHours = 6f;
    [Tooltip("Most flights in a connection (3 = up to two changes).")]
    [Range(1, 3)] public int maxFlights = 3;
    [Tooltip("Only connections whose flights share one operator (anonymised operators, ~19% of flights, are left out then). Also on the card.")]
    public bool sameAirlineOnly = false;

    /// <summary>A real connection being shown: the day's options between two airports and the one on the card.</summary>
    public class RealConnection
    {
        public GraphNode from;
        public GraphNode to;
        public List<ConnectionSearch.Itinerary> options = new List<ConnectionSearch.Itinerary>();
        public int index = -1;
        /// <summary>The panel's time was after the day's last option: the last one is shown.</summary>
        public bool noneAfterNow;
        public bool sameAirline;
        /// <summary>The network's route (all months), shown when no real connection exists.</summary>
        public List<GraphNode> networkRoute = new List<GraphNode>();
        public ConnectionSearch.Itinerary Current => index >= 0 && index < options.Count ? options[index] : null;
    }

    /// <summary>The real connection on the card, or null.</summary>
    public RealConnection Current { get; private set; }

    private ConnectionSearch search;
    private VoiceAssistant voice;
    private IReadOnlyList<FlightData> searchedFlights;

    private InputAction rightButton;
    private InputAction leftButton;
    private NearFarInteractor[] interactors = new NearFarInteractor[0];

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (infoPanel == null) infoPanel = FindFirstObjectByType<GraphInfoPanel>();
        rightButton = new InputAction("Connection Right", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        leftButton = new InputAction("Connection Left", InputActionType.Button, "<XRController>{LeftHand}/primaryButton");
    }

    private void OnEnable()
    {
        rightButton.Enable();
        leftButton.Enable();
        if (selector == null) return;
        selector.ConnectionRequested += Connect;
        selector.NodeSelected += OnOtherSelection;
        selector.EdgeSelected += OnOtherSelection;
        selector.SelectionCleared += OnCleared;
    }

    private void OnDisable()
    {
        rightButton.Disable();
        leftButton.Disable();
        if (selector == null) return;
        selector.ConnectionRequested -= Connect;
        selector.NodeSelected -= OnOtherSelection;
        selector.EdgeSelected -= OnOtherSelection;
        selector.SelectionCleared -= OnCleared;
    }

    private void OnOtherSelection(GraphNode node) => Current = null;
    private void OnOtherSelection(GraphEdge edge) => Current = null;
    private void OnCleared() => Current = null;

    private void OnDestroy()
    {
        rightButton.Dispose();
        leftButton.Dispose();
    }

    private void Start()
    {
        // Include inactive: the hands rig switches between hand and controller interactors at runtime.
        interactors = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    private void Update()
    {
        if (graph == null || selector == null || !graph.IsLoaded) return;
        // A real connection belongs to the live flights: it goes when their panel closes.
        if (Current != null && !LiveFlightsShown) selector.ClearSelection();
        if (GuidedTour.InputLocked) return;
        if (rightButton.WasPressedThisFrame()) TryConnect(InteractorHandedness.Right);
        if (leftButton.WasPressedThisFrame()) TryConnect(InteractorHandedness.Left);
    }

    private void TryConnect(InteractorHandedness hand)
    {
        GraphNode from = selector.SelectedNode;
        if (from == null)
        {
            Notice("Select an airport first, then point at a second one and press <b>A</b> / <b>X</b>.");
            return;
        }
        GraphNode to = HoveredNode(hand, out NearFarInteractor by);
        if (to == null || to == from)
        {
            Notice("Point at a second airport, then press <b>A</b> / <b>X</b>.");
            return;
        }
        if (ShowConnection(from, to)) ControllerHaptics.Pulse(by, ControllerHaptics.FoundAmplitude, ControllerHaptics.FoundSeconds);
    }

    /// <summary>Tells the user on the voice assistant's caption (the Console too); only the Console when the scene has no assistant.</summary>
    private void Notice(string text)
    {
        Debug.Log("ConnectionFinder: " + text);
        if (voice == null) voice = FindFirstObjectByType<VoiceAssistant>();
        if (voice != null) voice.ShowCaption(text, 4f);
    }

    /// <summary>Node currently hovered by that hand's Near-Far interactor, or null.</summary>
    private GraphNode HoveredNode(InteractorHandedness hand, out NearFarInteractor by)
    {
        by = null;
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled || interactor.handedness != hand) continue;
            foreach (IXRHoverInteractable hovered in interactor.interactablesHovered)
            {
                GraphNode node = hovered.transform.GetComponent<GraphNode>();
                if (node != null)
                {
                    by = interactor;
                    return node;
                }
            }
        }
        return null;
    }

    public void Connect(GraphNode from, GraphNode to) => ShowConnection(from, to);

    /// <summary>
    /// Connect, telling whether a connection is shown: true for a route, or a real connection
    /// of the day while the live flights are open; false when there is none (said on the caption,
    /// or on the real connection card).
    /// </summary>
    public bool ShowConnection(GraphNode from, GraphNode to)
    {
        if (LiveFlightsShown)
        {
            ShowRealConnection(from, to);
            return Current != null && Current.Current != null;
        }
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (!FindPath(from, to, nodes, edges))
        {
            Notice($"No route between {from.ShortCode} and {to.ShortCode} in this graph.");
            return false;
        }
        selector.SelectPath(nodes, edges);
        return true;
    }

    // ---- Real connections (TASKS B7) --------------------------------------------

    private FlightSimulator Simulator => flights != null ? flights.simulator : null;

    /// <summary>True while the live flights panel is open with its flights loaded.</summary>
    public bool LiveFlightsShown
    {
        get
        {
            if (flights == null) flights = FindFirstObjectByType<FlightPanel>();
            return flights != null && flights.IsOpen && Simulator != null && Simulator.IsReady && Simulator.Flights != null;
        }
    }

    private ConnectionSearch.Settings Settings => new ConnectionSearch.Settings
    {
        minConnectionSec = Mathf.RoundToInt(minConnectionMinutes * 60f),
        maxWaitSec = Mathf.RoundToInt(maxWaitHours * 3600f),
        maxLegs = maxFlights,
        sameAirline = sameAirlineOnly,
    };

    private ConnectionSearch Search
    {
        get
        {
            IReadOnlyList<FlightData> all = Simulator.Flights;
            if (search == null || !ReferenceEquals(all, searchedFlights))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                search = new ConnectionSearch(all);
                searchedFlights = all;
                Debug.Log($"ConnectionFinder: real connections over {search.PassengerFlights} passenger flights " +
                          $"({search.OtherFlights} cargo / business / other left out" + (search.HasSegments ? "" : "; no market segments in the file, all used") +
                          $"), indexed in {watch.Elapsed.TotalMilliseconds:F0} ms");
            }
            return search;
        }
    }

    /// <summary>The day's real connections from `from` to `to`; shows the first one leaving after the panel's time.</summary>
    public void ShowRealConnection(GraphNode from, GraphNode to)
    {
        var c = new RealConnection { from = from, to = to, sameAirline = sameAirlineOnly };
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (FindPath(from, to, nodes, edges)) c.networkRoute = nodes;
        Find(c, Simulator.SimTime);
        Current = c;
        Display();
    }

    // Options of c for the current settings; the shown one is the first leaving at or after `after` (s).
    private void Find(RealConnection c, float after)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        c.options = Search.Options(c.from.id, c.to.id, Settings);
        // The day's connections: the first flight leaves on the day (the file also holds the evening before).
        c.options.RemoveAll(o => o.Departure < 0);
        c.index = c.options.FindIndex(o => o.Departure >= after);
        c.noneAfterNow = c.index < 0 && c.options.Count > 0;
        if (c.noneAfterNow) c.index = c.options.Count - 1;
        Debug.Log($"ConnectionFinder: {c.from.ShortCode} -> {c.to.ShortCode}: {c.options.Count} real connection(s) that day" +
                  (c.sameAirline ? " (same airline)" : "") + $", found in {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    // Shows Current: its airports and routes highlighted, its flights marked, its card.
    private void Display()
    {
        RealConnection c = Current;
        if (c == null) return;
        ConnectionSearch.Itinerary it = c.Current;
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (it != null)
        {
            nodes.Add(c.from);
            foreach (FlightData leg in it.legs)
            {
                if (!graph.Nodes.TryGetValue(leg.destination, out GraphNode next)) continue;
                GraphEdge e = FindEdge(nodes[nodes.Count - 1].id, next.id);
                if (e != null) edges.Add(e);
                nodes.Add(next);
            }
        }
        else
        {
            nodes.AddRange(c.networkRoute);
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                GraphEdge e = FindEdge(nodes[i].id, nodes[i + 1].id);
                if (e != null) edges.Add(e);
            }
        }
        // Selecting the route ends any earlier connection (events), so keep ours across it.
        RealConnection keep = c;
        if (nodes.Count >= 2 && edges.Count == nodes.Count - 1) selector.SelectPath(nodes, edges);
        else selector.SelectNode(c.from);
        Current = keep;
        Simulator.ShowItinerary(it != null ? it.legs : null);
        if (infoPanel != null) infoPanel.ShowConnection(this, Simulator);
    }

    /// <summary>The option before the shown one (earlier departure).</summary>
    public void Earlier() => Step(-1);

    /// <summary>The option after the shown one (later departure).</summary>
    public void Later() => Step(+1);

    private void Step(int delta)
    {
        RealConnection c = Current;
        if (c == null || c.options.Count == 0) return;
        int next = Mathf.Clamp(c.index + delta, 0, c.options.Count - 1);
        if (next == c.index) return;
        c.index = next;
        c.noneAfterNow = false;
        Display();
    }

    /// <summary>Plays the shown connection: the panel's time goes to ten minutes before its first flight leaves.</summary>
    public void Watch()
    {
        ConnectionSearch.Itinerary it = Current?.Current;
        if (it == null || flights == null) return;
        Simulator.Seek(Mathf.Max(0f, it.Departure - 600f));
        flights.SetPlaying(true);
    }

    /// <summary>Same airline only on / off: searches again, keeping about the same departure.</summary>
    public void SetSameAirline(bool value)
    {
        RealConnection c = Current;
        sameAirlineOnly = value;
        if (c == null) return;
        float after = c.Current != null ? c.Current.Departure : Simulator.SimTime;
        c.sameAirline = value;
        Find(c, after);
        Display();
    }

    private GraphEdge FindEdge(string a, string b)
    {
        foreach (GraphEdge e in graph.EdgesOf(a))
        {
            if ((e.sourceId == a && e.targetId == b) || (e.sourceId == b && e.targetId == a)) return e;
        }
        return null;
    }

    /// <summary>Fewest-edges route between two airports, without selecting it (used by the guided tour).</summary>
    public bool TryFindPath(GraphNode from, GraphNode to, List<GraphNode> nodes, List<GraphEdge> edges)
    {
        return FindPath(from, to, nodes, edges);
    }

    /// <summary>Fewest-edges route (BFS). Fills nodes (from..to) and edges (legs in order).</summary>
    private bool FindPath(GraphNode from, GraphNode to, List<GraphNode> nodes, List<GraphEdge> edges)
    {
        var cameVia = new Dictionary<string, GraphEdge>();
        var visited = new HashSet<string> { from.id };
        var queue = new Queue<string>();
        queue.Enqueue(from.id);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (current == to.id) break;

            var neighbours = new List<GraphEdge>(graph.EdgesOf(current));
            neighbours.Sort((x, y) => y.weight.CompareTo(x.weight));
            foreach (GraphEdge e in neighbours)
            {
                string next = e.sourceId == current ? e.targetId : e.sourceId;
                if (visited.Add(next))
                {
                    cameVia[next] = e;
                    queue.Enqueue(next);
                }
            }
        }
        if (!visited.Contains(to.id)) return false;

        string id = to.id;
        nodes.Add(to);
        while (id != from.id)
        {
            GraphEdge e = cameVia[id];
            edges.Add(e);
            id = e.sourceId == id ? e.targetId : e.sourceId;
            nodes.Add(graph.Nodes[id]);
        }
        nodes.Reverse();
        edges.Reverse();
        return true;
    }
}
