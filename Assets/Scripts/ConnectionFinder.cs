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
/// </summary>
public class ConnectionFinder : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;

    private InputAction rightButton;
    private InputAction leftButton;
    private NearFarInteractor[] interactors = new NearFarInteractor[0];

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        rightButton = new InputAction("Connection Right", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        leftButton = new InputAction("Connection Left", InputActionType.Button, "<XRController>{LeftHand}/primaryButton");
    }

    private void OnEnable()
    {
        rightButton.Enable();
        leftButton.Enable();
        if (selector != null) selector.ConnectionRequested += Connect;
    }

    private void OnDisable()
    {
        rightButton.Disable();
        leftButton.Disable();
        if (selector != null) selector.ConnectionRequested -= Connect;
    }

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
        if (graph == null || selector == null || !graph.IsLoaded || GuidedTour.InputLocked) return;
        if (rightButton.WasPressedThisFrame()) TryConnect(InteractorHandedness.Right);
        if (leftButton.WasPressedThisFrame()) TryConnect(InteractorHandedness.Left);
    }

    private void TryConnect(InteractorHandedness hand)
    {
        GraphNode from = selector.SelectedNode;
        if (from == null)
        {
            Debug.Log("ConnectionFinder: select a node first (grip / pinch), then point at another and press A / X.");
            return;
        }
        GraphNode to = HoveredNode(hand);
        if (to == null || to == from) return;
        Connect(from, to);
    }

    /// <summary>Node currently hovered by that hand's Near-Far interactor, or null.</summary>
    private GraphNode HoveredNode(InteractorHandedness hand)
    {
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled || interactor.handedness != hand) continue;
            foreach (IXRHoverInteractable hovered in interactor.interactablesHovered)
            {
                GraphNode node = hovered.transform.GetComponent<GraphNode>();
                if (node != null) return node;
            }
        }
        return null;
    }

    public void Connect(GraphNode from, GraphNode to)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (!FindPath(from, to, nodes, edges))
        {
            Debug.LogWarning($"ConnectionFinder: no route between '{from.label}' and '{to.label}' in this graph.");
            return;
        }
        selector.SelectPath(nodes, edges);
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
