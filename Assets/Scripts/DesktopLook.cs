using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Camera of the Desktop condition (DesktopMode): you stay inside the graph as in the headset
/// and look around with the mouse.
///   Look:  hold the right mouse button and drag, or the arrow keys
///   Move:  W A S D (Shift faster), Q / E down / up, scroll wheel forward / back
///   Go to: double-click an airport (the desktop counterpart of teleporting next to it)
/// H (GraphRecenter) still goes to the graph centre. Moves are applied as steps to the camera,
/// so the room's walls (MovementBounds) and turns by the tour or the voice assistant keep
/// working on it. The eyes stay between the floor and the walls' top. During the guided tour
/// you can still look around and move, as in the headset, where the tour locks teleporting
/// but not your head or your steps; only the double-click glide (the teleport's counterpart)
/// waits for the tour to end. Reduce motion makes the double-click a jump.
/// </summary>
public class DesktopLook : MonoBehaviour
{
    [Tooltip("Degrees turned per pixel of mouse drag (right button held).")]
    public float mouseSensitivity = 0.15f;
    [Tooltip("Degrees per second with the arrow keys.")]
    public float keyTurnSpeed = 80f;
    [Tooltip("Walking speed (m/s) with W A S D / Q E; Shift multiplies it.")]
    public float moveSpeed = 1.4f;
    public float fastMultiplier = 3f;
    [Tooltip("Metres per scroll-wheel notch.")]
    public float scrollStep = 0.25f;
    [Tooltip("Lowest and highest eye height above the floor (m); the room's walls are 3 m high.")]
    public float minEyeHeight = 0.3f;
    public float maxEyeHeight = 2.9f;
    [Tooltip("Double-click on an airport: stop this far (m) in front of it.")]
    public float flyStopDistance = 1f;
    [Tooltip("Spring smooth time (s) of that glide.")]
    public float flySmoothTime = 0.18f;
    [Tooltip("Two clicks on the same airport closer than this (s) are a double-click.")]
    public float doubleClickSeconds = 0.35f;

    public GraphLoader graph;

    private Camera cam;
    private float yaw;
    private float pitch;
    private Quaternion lastSetRotation;
    private bool flying;
    private Vector3 flyTarget;
    private Vector3 flyVelocity;
    private float flyYaw;
    private float flyPitch;
    private float yawVelocity;
    private float pitchVelocity;
    private GraphNode lastClicked;
    private float lastClickTime = -10f;

    /// <summary>True while gliding to an airport after a double-click.</summary>
    public bool Flying => flying;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        SyncAngles();
    }

    private void SyncAngles()
    {
        Vector3 e = transform.rotation.eulerAngles;
        yaw = e.y;
        pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -85f, 85f);
        lastSetRotation = transform.rotation;
    }

    private void Update()
    {
        // Turned by someone else (tour, voice "show X", GraphRecenter): continue from there.
        if (Quaternion.Angle(transform.rotation, lastSetRotation) > 0.01f) SyncAngles();
        // The tour locks teleporting, so the glide waits; looking and moving stay (B36 fix).
        bool touring = GuidedTour.InputLocked;
        if (touring) flying = false;

        float dt = Time.unscaledDeltaTime;
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;

        float dYaw = 0f;
        float dPitch = 0f;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 d = mouse.delta.ReadValue();
            dYaw += d.x * mouseSensitivity;
            dPitch -= d.y * mouseSensitivity;
        }
        Vector3 move = Vector3.zero;
        bool fast = false;
        if (kb != null)
        {
            if (kb.leftArrowKey.isPressed) dYaw -= keyTurnSpeed * dt;
            if (kb.rightArrowKey.isPressed) dYaw += keyTurnSpeed * dt;
            if (kb.upArrowKey.isPressed) dPitch -= keyTurnSpeed * dt;
            if (kb.downArrowKey.isPressed) dPitch += keyTurnSpeed * dt;
            if (kb.wKey.isPressed) move.z += 1f;
            if (kb.sKey.isPressed) move.z -= 1f;
            if (kb.dKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed) move.x -= 1f;
            if (kb.eKey.isPressed) move.y += 1f;
            if (kb.qKey.isPressed) move.y -= 1f;
            fast = kb.shiftKey.isPressed;
        }
        float speed = moveSpeed * (fast ? fastMultiplier : 1f);
        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector3 step = (heading * new Vector3(move.x, 0f, move.z) + Vector3.up * move.y) * speed * dt;
        if (mouse != null)
        {
            // Windows reports 120 per wheel notch. Over a panel the wheel and clicks belong to it (asked only then).
            float notches = Mathf.Clamp(mouse.scroll.ReadValue().y / 120f, -3f, 3f);
            bool scrolled = Mathf.Abs(notches) > 0.001f;
            bool clicked = mouse.leftButton.wasPressedThisFrame;
            if ((scrolled || clicked) && !UIKit.PointerOverUI(mouse.position.ReadValue()))
            {
                if (scrolled) step += transform.forward * notches * scrollStep * (fast ? fastMultiplier : 1f);
                if (clicked && !touring) OnClick(mouse.position.ReadValue());
            }
        }

        bool manual = dYaw != 0f || dPitch != 0f || step != Vector3.zero;
        if (manual) flying = false; // the participant takes over
        if (flying)
        {
            Vector3 next = Vector3.SmoothDamp(transform.position, flyTarget, ref flyVelocity, flySmoothTime, Mathf.Infinity, dt);
            step = next - transform.position;
            float newYaw = Mathf.SmoothDampAngle(yaw, flyYaw, ref yawVelocity, flySmoothTime, Mathf.Infinity, dt);
            float newPitch = Mathf.SmoothDampAngle(pitch, flyPitch, ref pitchVelocity, flySmoothTime, Mathf.Infinity, dt);
            dYaw = Mathf.DeltaAngle(yaw, newYaw);
            dPitch = newPitch - pitch;
            if ((flyTarget - next).sqrMagnitude < 1e-6f && Mathf.Abs(Mathf.DeltaAngle(newYaw, flyYaw)) < 0.1f &&
                Mathf.Abs(newPitch - flyPitch) < 0.1f)
            {
                flying = false;
            }
        }
        Step(step, dYaw, dPitch);
    }

    // A second click on the same airport soon after the first: go to it.
    private void OnClick(Vector2 screenPosition)
    {
        if (cam == null) return;
        GraphNode node = null;
        if (Physics.Raycast(cam.ScreenPointToRay(screenPosition), out RaycastHit hit, 500f))
        {
            node = hit.collider.GetComponentInParent<GraphNode>();
        }
        if (node != null && node == lastClicked && Time.unscaledTime - lastClickTime <= doubleClickSeconds)
        {
            FlyTo(node);
            lastClicked = null;
            return;
        }
        lastClicked = node;
        lastClickTime = Time.unscaledTime;
    }

    /// <summary>Moves by `worldStep` and turns by yaw / pitch (degrees), keeping the eyes in the room's height.</summary>
    public void Step(Vector3 worldStep, float dYaw, float dPitch)
    {
        yaw += dYaw;
        pitch = Mathf.Clamp(pitch + dPitch, -85f, 85f);
        Vector3 position = transform.position + worldStep;
        position.y = ClampHeight(position.y);
        transform.SetPositionAndRotation(position, Quaternion.Euler(pitch, yaw, 0f));
        lastSetRotation = transform.rotation;
    }

    /// <summary>Glides to a spot in front of the airport, facing it (a jump with Reduce motion).</summary>
    public void FlyTo(GraphNode node)
    {
        if (node == null) return;
        Vector3 airport = node.transform.position;
        Vector3 toAirport = airport - transform.position;
        if (toAirport.sqrMagnitude < 1e-4f) toAirport = transform.forward;
        flyTarget = airport - toAirport.normalized * flyStopDistance;
        flyTarget.y = ClampHeight(flyTarget.y);
        Vector3 e = Quaternion.LookRotation(airport - flyTarget, Vector3.up).eulerAngles;
        flyYaw = e.y;
        flyPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -85f, 85f);
        Debug.Log($"DesktopLook: going to {node.ShortCode}");
        if (Comfort.ReduceMotion)
        {
            flying = false;
            Step(flyTarget - transform.position, Mathf.DeltaAngle(yaw, flyYaw), flyPitch - pitch);
            return;
        }
        flying = true;
        flyVelocity = Vector3.zero;
        yawVelocity = 0f;
        pitchVelocity = 0f;
    }

    private float ClampHeight(float y)
    {
        float floor = graph != null ? graph.FloorY : 0f;
        return Mathf.Clamp(y, floor + minEyeHeight, floor + maxEyeHeight);
    }
}
