using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

/// <summary>
/// Re-centre (TASKS B3): one button teleports the viewer to the graph's centre and turns
/// them to its front (+Z, the biggest hub in layout_3d.py). The graph stays where it is;
/// the XR Origin moves, in one snap like a teleport. Right thumbstick click (R3;
/// recenterBinding), H in the Editor, or the button on the dashboard's Controls page.
/// With matchEyeHeight (default; seated use / different heights) the eyes also go to the
/// centre's height, and the floors (teleport areas / anchors) move by the same amount so
/// the virtual floor stays where the real one is and later teleports keep that height.
/// Off = only the horizontal position changes, like a normal teleport.
/// </summary>
public class GraphRecenter : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Controller button that re-centres (Input System path). Default: right thumbstick click (R3). Empty = no controller button.")]
    public string recenterBinding = "<XRController>{RightHand}/{Primary2DAxisClick}";
    [Tooltip("Also turn the viewer to face the graph's front (+Z, the biggest hub).")]
    public bool faceGraphFront = true;
    [Tooltip("Also put the eyes at the graph centre's height (seated use), moving the floors along. Off = only move horizontally.")]
    public bool matchEyeHeight = true;

    /// <summary>Raised after the viewer has moved.</summary>
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

    /// <summary>Teleports the viewer to the graph centre and, with faceGraphFront, turns them to its front.</summary>
    public void Recenter()
    {
        if (graph == null || !graph.IsLoaded) return;
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        if (origin == null || origin.Camera == null) return;
        Transform head = origin.Camera.transform;
        Transform centre = graph.transform;

        Vector3 target = centre.position;
        if (!matchEyeHeight) target.y = head.position.y; // a normal teleport: keep the current eye height
        float originHeightBefore = origin.Origin.transform.position.y;

        origin.MoveCameraToWorldLocation(target);
        if (faceGraphFront)
        {
            Vector3 look = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            Vector3 front = Vector3.ProjectOnPlane(centre.forward, Vector3.up);
            if (look.sqrMagnitude > 1e-6f && front.sqrMagnitude > 1e-6f)
            {
                origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(look, front, Vector3.up));
            }
        }

        float heightChange = origin.Origin.transform.position.y - originHeightBefore;
        int floorsMoved = matchEyeHeight && Mathf.Abs(heightChange) > 0.001f ? MoveFloors(heightChange) : 0;

        UISounds.Play(UISounds.Open, head.position + head.forward * 0.5f);
        Debug.Log($"GraphRecenter: matchEyeHeight={matchEyeHeight}, graph centre y={centre.position.y:F2}, " +
                  $"eyes now y={head.position.y:F2}, origin moved {heightChange:+0.00;-0.00} m vertically, floors moved: {floorsMoved}");
        Recentered?.Invoke();
    }

    /// <summary>Moves every teleport area / anchor (each object once, even if nested) by dy; returns how many moved.</summary>
    private static int MoveFloors(float dy)
    {
        int moved = 0;
        var floors = new HashSet<Transform>();
        foreach (BaseTeleportationInteractable floor in FindObjectsByType<BaseTeleportationInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            floors.Add(floor.transform);
        }
        foreach (Transform t in floors)
        {
            bool nested = false;
            for (Transform p = t.parent; p != null && !nested; p = p.parent) nested = floors.Contains(p);
            if (nested) continue;
            t.position += Vector3.up * dy;
            moved++;
        }
        return moved;
    }
}
