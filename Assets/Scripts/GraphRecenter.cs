using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Seated use / different heights (TASKS B3): one button moves the graph's centre to the
/// viewer's head and turns its front (+Z, the biggest hub in layout_3d.py) to where they
/// look. Right thumbstick click (R3; recenterBinding), H in the Editor, or the button on
/// the dashboard's Controls page.
/// The graph snaps instead of gliding (a world sliding around you is uncomfortable in VR),
/// and is lowered only as far as keeps every airport above the floor.
/// </summary>
public class GraphRecenter : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Head (camera). Defaults to Camera.main (the XR head camera).")]
    public Transform head;
    [Tooltip("Controller button that re-centres (Input System path). Default: right thumbstick click (R3). Empty = no controller button.")]
    public string recenterBinding = "<XRController>{RightHand}/{Primary2DAxisClick}";
    [Tooltip("Also turn the graph so its front is where you look (otherwise only its position changes).")]
    public bool faceViewer = true;
    [Tooltip("Lowest height (m) an airport may have after re-centring; seated eyes are lower than the layout assumes.")]
    public float floorClearance = 0.25f;

    /// <summary>Raised after the graph has moved.</summary>
    public event System.Action Recentered;

    private InputAction recenterAction;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        recenterAction = new InputAction("Recenter Graph", InputActionType.Button);
        if (!string.IsNullOrEmpty(recenterBinding)) recenterAction.AddBinding(recenterBinding);
        recenterAction.AddBinding("<Keyboard>/h");
    }

    private void OnEnable() => recenterAction.Enable();

    private void OnDisable() => recenterAction.Disable();

    private void OnDestroy() => recenterAction.Dispose();

    private void Update()
    {
        if (recenterAction.WasPressedThisFrame() && !GuidedTour.InputLocked) Recenter();
    }

    /// <summary>Moves the graph to the head (height clamped above the floor) and, with faceViewer, turns it to the gaze.</summary>
    public void Recenter()
    {
        if (graph == null || !graph.IsLoaded) return;
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (head == null) return;

        Transform t = graph.transform;
        // How far the lowest airport sits below the centre (turning around Y keeps heights).
        float lowest = 0f;
        foreach (GraphNode node in graph.Nodes.Values)
        {
            lowest = Mathf.Min(lowest, node.transform.position.y - t.position.y);
        }
        Vector3 target = head.position;
        target.y = Mathf.Max(target.y, floorClearance - lowest);
        t.position = target;

        if (faceViewer)
        {
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        // Edge arcs are world-space points around the centre: draw them again from the new airport positions.
        graph.RebuildEdges(graph.Edges);
        UISounds.Play(UISounds.Open, head.position + head.forward * 0.5f);
        Debug.Log($"GraphRecenter: graph centre at {target.y:F2} m (head {head.position.y:F2} m)");
        Recentered?.Invoke();
    }
}
