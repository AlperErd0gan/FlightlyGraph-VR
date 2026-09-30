using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// XR input for the graph, the same with hands (pinch) and controllers (grip):
/// - node: once the graph is loaded, every node gets an XRSimpleInteractable.
///   Nothing selected yet: select on it selects the node. Another node already
///   selected: a short select selects this one instead; holding it for
///   connectHoldSeconds shows the connection between the two (ConnectionFinder),
///   so routes work without an A / X button. The held node grows while holding.
/// - empty space: edges have no colliders, so a short select (tap) that hovers
///   nothing tests the ray against the edge arcs (GraphSelector.PickEdgeAlongRay);
///   a miss clears the selection. Holding on empty space is left to HandTeleport.
/// </summary>
public class XRGraphInput : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Optional. Enables hold-to-connect; found in the scene if unset.")]
    public ConnectionFinder connectionFinder;
    [Tooltip("Collider radius relative to the visual sphere; >1 makes small nodes easier to hit, but too large overlaps dense regions (Europe).")]
    public float colliderScale = 1.5f;
    [Tooltip("Turn off GraphSelector's mouse picking. Keep on for XR: the XR Interaction Simulator also uses the mouse.")]
    public bool disableMouseSelection = true;

    [Header("Gestures")]
    [Tooltip("Hold select on a second node this long to show the connection to the selected node.")]
    public float connectHoldSeconds = 0.6f;
    [Tooltip("How much the held node grows while holding (0.3 = +30%), as progress feedback.")]
    public float holdGrow = 0.3f;
    [Tooltip("A select on empty space shorter than this is a tap (edge pick / clear). Keep below HandTeleport.holdSeconds.")]
    public float tapMaxSeconds = 0.35f;

    [Header("Edges")]
    [Tooltip("Max angle (degrees, seen from the hand) between the ray and an edge for it to count as hit.")]
    public float edgePickAngle = 1.5f;
    [Tooltip("A tap on empty space (no node, no edge) clears the selection.")]
    public bool clearOnEmptySelect = true;

    /// <summary>Select held on a node while another node is selected.</summary>
    private class PendingHold
    {
        public GraphNode node;
        public float startTime;
        public Vector3 baseScale;
        public bool connected;
    }

    private NearFarInteractor[] interactors = new NearFarInteractor[0];
    private readonly Dictionary<IXRSelectInteractor, PendingHold> holds = new Dictionary<IXRSelectInteractor, PendingHold>();
    // Press time of selects that started on empty space, per interactor.
    private readonly Dictionary<NearFarInteractor, float> emptyPresses = new Dictionary<NearFarInteractor, float>();
    private bool wired;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (connectionFinder == null) connectionFinder = FindFirstObjectByType<ConnectionFinder>();
    }

    private void Start()
    {
        if (selector != null && disableMouseSelection) selector.mouseInput = false;
        // Include inactive: the hands rig switches between hand and controller interactors at runtime.
        interactors = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (interactors.Length == 0)
        {
            Debug.LogWarning("XRGraphInput: no NearFarInteractor in scene; edge selection disabled.");
        }
    }

    private void Update()
    {
        if (graph == null || selector == null || !graph.IsLoaded) return;

        if (!wired)
        {
            WireNodes();
        }

        UpdateHolds();
        UpdateEmptySpaceTaps();
    }

    private void UpdateHolds()
    {
        foreach (PendingHold hold in holds.Values)
        {
            if (hold.connected) continue;
            float progress = Mathf.Clamp01((Time.time - hold.startTime) / connectHoldSeconds);
            hold.node.transform.localScale = hold.baseScale * (1f + holdGrow * progress);
            if (progress >= 1f)
            {
                hold.connected = true;
                hold.node.transform.localScale = hold.baseScale;
                GraphNode from = selector.SelectedNode;
                if (from != null && from != hold.node) connectionFinder.Connect(from, hold.node);
            }
        }
    }

    private void UpdateEmptySpaceTaps()
    {
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled) continue;

            // Hovering a node (or anything else interactable): XRI handles that select itself.
            if (interactor.selectInput.ReadWasPerformedThisFrame() && !interactor.hasHover && !interactor.hasSelection)
            {
                emptyPresses[interactor] = Time.time;
            }

            if (!interactor.selectInput.ReadWasCompletedThisFrame()) continue;
            if (!emptyPresses.TryGetValue(interactor, out float pressTime)) continue;
            emptyPresses.Remove(interactor);
            if (Time.time - pressTime > tapMaxSeconds) continue; // a hold: teleport aiming, not a tap

            Transform origin = interactor.curveOrigin;
            if (origin == null) continue;
            GraphEdge edge = selector.PickEdgeAlongRay(new Ray(origin.position, origin.forward), edgePickAngle);
            if (edge != null)
            {
                selector.SelectEdge(edge);
            }
            else if (clearOnEmptySelect)
            {
                selector.ClearSelection();
            }
        }
    }

    private void WireNodes()
    {
        foreach (GraphNode node in graph.Nodes.Values)
        {
            SphereCollider col = node.GetComponent<SphereCollider>();
            if (col != null) col.radius = 0.5f * colliderScale;

            // XRBaseInteractable collects colliders in Awake, so the collider must exist first.
            XRSimpleInteractable interactable = node.gameObject.AddComponent<XRSimpleInteractable>();
            GraphNode captured = node;
            interactable.selectEntered.AddListener(args => OnNodeSelectEntered(captured, args.interactorObject));
            interactable.selectExited.AddListener(args => OnNodeSelectExited(args.interactorObject));
        }
        wired = true;
        Debug.Log($"XRGraphInput: {graph.Nodes.Count} nodes made XR-selectable, {interactors.Length} Near-Far interactors found.");
    }

    private void OnNodeSelectEntered(GraphNode node, IXRSelectInteractor interactor)
    {
        GraphNode selected = selector.SelectedNode;
        if (connectionFinder == null || selected == null || selected == node)
        {
            selector.SelectNode(node);
            return;
        }
        // Another node is selected: wait to see whether this is a tap (select) or a hold (connect).
        holds[interactor] = new PendingHold
        {
            node = node,
            startTime = Time.time,
            baseScale = node.transform.localScale,
        };
    }

    private void OnNodeSelectExited(IXRSelectInteractor interactor)
    {
        if (!holds.TryGetValue(interactor, out PendingHold hold)) return;
        holds.Remove(interactor);
        hold.node.transform.localScale = hold.baseScale;
        if (!hold.connected)
        {
            selector.SelectNode(hold.node);
        }
    }
}
