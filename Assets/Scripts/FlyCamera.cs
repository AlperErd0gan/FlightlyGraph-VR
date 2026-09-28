using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Desktop navigation for exploring the graph before VR is wired up.
///   Move:   W/A/S/D forward/left/back/right, Q/E down/up, Shift = fast
///   Look:   arrow keys (left/right = yaw, up/down = pitch), or hold right mouse and drag
///   Zoom:   mouse scroll wheel dollies along the view direction (also Z / X keys)
///   Speed:  Ctrl + scroll, or + / - keys, adjusts move speed
///   Reset:  R returns to the start pose
/// Uses the new Input System (project has legacy input disabled).
/// </summary>
public class FlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float fastMultiplier = 4f;
    public float minSpeed = 0.5f;
    public float maxSpeed = 50f;
    [Tooltip("If true, W/S move along the horizontal plane instead of the view direction (map-friendly).")]
    public bool moveOnHorizontalPlane = true;

    [Header("Zoom")]
    [Tooltip("World units moved per scroll notch.")]
    public float zoomStep = 1.5f;
    [Tooltip("Zoom speed when holding Z / X, units per second.")]
    public float keyZoomSpeed = 8f;

    [Header("Look")]
    public float mouseSensitivity = 0.15f;
    [Tooltip("Degrees per second when turning with the arrow keys.")]
    public float keyTurnSpeed = 90f;
    [Tooltip("Higher = snappier rotation. 0 = instant.")]
    public float lookSmoothing = 12f;

    private float yaw;
    private float pitch;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private void Start()
    {
        startPosition = transform.position;
        startRotation = transform.rotation;
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null)
        {
            return;
        }

        if (kb.rKey.wasPressedThisFrame)
        {
            transform.SetPositionAndRotation(startPosition, startRotation);
            Vector3 e = startRotation.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
            return;
        }

        UpdateLook(kb, mouse);
        UpdateZoomAndSpeed(kb, mouse);
        UpdateMove(kb);
    }

    private void UpdateLook(Keyboard kb, Mouse mouse)
    {
        // Arrow keys: continuous turn.
        float turn = keyTurnSpeed * Time.deltaTime;
        if (kb.leftArrowKey.isPressed) yaw -= turn;
        if (kb.rightArrowKey.isPressed) yaw += turn;
        if (kb.upArrowKey.isPressed) pitch -= turn;
        if (kb.downArrowKey.isPressed) pitch += turn;

        // Right mouse held: drag to look. Left click stays free for node selection.
        if (mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity;
            pitch -= delta.y * mouseSensitivity;
        }

        pitch = Mathf.Clamp(pitch, -89f, 89f);

        Quaternion target = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = lookSmoothing <= 0f
            ? target
            : Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-lookSmoothing * Time.deltaTime));
    }

    private void UpdateZoomAndSpeed(Keyboard kb, Mouse mouse)
    {
        // Mouse scroll values vary by platform (±120 on Windows, small floats on macOS): use the sign only.
        float scroll = mouse.scroll.ReadValue().y;
        float notch = Mathf.Abs(scroll) > 0.01f ? Mathf.Sign(scroll) : 0f;

        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        float speedChange = ctrl ? notch : 0f;
        if (kb.numpadPlusKey.isPressed || kb.equalsKey.isPressed) speedChange += Time.deltaTime * 3f;
        if (kb.numpadMinusKey.isPressed || kb.minusKey.isPressed) speedChange -= Time.deltaTime * 3f;
        if (speedChange != 0f)
        {
            moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.2f, speedChange), minSpeed, maxSpeed);
        }

        float zoom = ctrl ? 0f : notch * zoomStep;
        if (kb.zKey.isPressed) zoom += keyZoomSpeed * Time.deltaTime;
        if (kb.xKey.isPressed) zoom -= keyZoomSpeed * Time.deltaTime;
        if (zoom != 0f)
        {
            transform.position += transform.forward * zoom;
        }
    }

    private void UpdateMove(Keyboard kb)
    {
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        if (moveOnHorizontalPlane)
        {
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();
        }

        Vector3 dir = Vector3.zero;
        if (kb.wKey.isPressed) dir += forward;
        if (kb.sKey.isPressed) dir -= forward;
        if (kb.dKey.isPressed) dir += right;
        if (kb.aKey.isPressed) dir -= right;
        if (kb.eKey.isPressed) dir += Vector3.up;
        if (kb.qKey.isPressed) dir -= Vector3.up;

        if (dir.sqrMagnitude < 0.0001f)
        {
            return;
        }

        float speed = moveSpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);
        transform.position += dir.normalized * speed * Time.deltaTime;
    }
}
