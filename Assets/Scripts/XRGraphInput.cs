using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// XR input for the graph:
/// - nodes: once the graph is loaded, every node gets an XRSimpleInteractable, so
///   hand pinch / controller trigger on a node (via Near-Far Interactor) selects it;
/// - edges: edges have no colliders, so when a Near-Far Interactor presses select
///   while hovering nothing, its ray is tested against the edge arcs
///   (GraphSelector.PickEdgeAlongRay); a miss clears the selection.
/// </summary>
public class XRGraphInput : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Collider radius relative to the visual sphere; >1 makes small nodes easier to hit, but too large overlaps dense regions (Europe).")]
    public float colliderScale = 1.5f;
    [Tooltip("Turn off GraphSelector's mouse picking. Keep on for XR: the XR Interaction Simulator also uses the mouse.")]
    public bool disableMouseSelection = true;

    [Header("Edges")]
    [Tooltip("Max angle (degrees, seen from the hand) between the ray and an edge for it to count as hit.")]
    public float edgePickAngle = 1.5f;
    [Tooltip("Select pressed on empty space (no node, no edge) clears the selection.")]
    public bool clearOnEmptySelect = true;

    private NearFarInteractor[] interactors = new NearFarInteractor[0];
    private bool wired;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
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

        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled) continue;
            if (!interactor.selectInput.ReadWasPerformedThisFrame()) continue;
            // Hovering a node (or anything else interactable): XRI handles that select itself.
            if (interactor.hasHover || interactor.hasSelection) continue;

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
            interactable.selectEntered.AddListener(_ => selector.SelectNode(captured));
        }
        wired = true;
        Debug.Log($"XRGraphInput: {graph.Nodes.Count} nodes made XR-selectable, {interactors.Length} Near-Far interactors found.");
    }
}
