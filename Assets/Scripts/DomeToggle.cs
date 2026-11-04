using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Button for the dome (GeoMapView.SetDome): while the map view is shown, a small "Dome" button
/// floats low on the left of the view; it bends the map of Europe around you, and "Flat map"
/// brings the flat map back (B / the dashboard always open the flat map). Only for the
/// EUROCONTROL graph (*_ectrl files): the other datasets are worldwide. G does the same in the
/// Editor. Self-contained: it adds itself when the scene has a GraphLoader and changes no other script.
/// </summary>
public class DomeToggle : MonoBehaviour
{
    public GraphLoader graph;
    public GeoMapView map;
    [Tooltip("Key that switches flat map <-> dome while the map is shown (Input System path). Empty = none.")]
    public string toggleKey = "<Keyboard>/g";
    [Tooltip("Place of the button in front of the eyes (m): x right, y up, z forward; turned with the head around the vertical only.")]
    public Vector3 offset = new Vector3(-0.28f, -0.3f, 0.75f);
    [Tooltip("How quickly the button follows the head (higher = snappier).")]
    public float follow = 4f;

    private Canvas canvas;
    private Button button;
    private InputAction toggle;
    private float nextLookup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<DomeToggle>() != null) return;
        if (FindFirstObjectByType<GraphLoader>() == null) return;
        new GameObject("DomeToggle").AddComponent<DomeToggle>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (!string.IsNullOrEmpty(toggleKey)) toggle = new InputAction("Dome Toggle", InputActionType.Button, toggleKey);
        UIKit.EnsureXREventSystem();
        canvas = UIKit.WorldCanvas("Dome Toggle", new Vector2(150f, 52f));
        button = UIKit.Button(canvas.transform, "Dome", Toggle, 0f, 0f, 150f, 52f, UIKit.ButtonStyle.Secondary,
                              UIKit.BodySize, UIIcon.Shape.Globe);
        canvas.gameObject.SetActive(false);
    }

    private void OnEnable() => toggle?.Enable();

    private void OnDisable() => toggle?.Disable();

    private void OnDestroy()
    {
        toggle?.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
    }

    /// <summary>Only the EUROCONTROL graph (Europe) gets the dome.</summary>
    private bool Europe => graph != null && ((graph.nodesFileName ?? "").Contains("ectrl"));

    private void Toggle()
    {
        // Also mid-way through flat <-> dome: it turns around; map <-> 3D has to finish first.
        if (map != null && map.Ready && map.IsMap && (!map.Switching || map.ChangingShape)) map.SetDome(!map.IsDome);
    }

    private void LateUpdate()
    {
        if (map == null && Time.unscaledTime >= nextLookup)
        {
            nextLookup = Time.unscaledTime + 1f;
            map = FindFirstObjectByType<GeoMapView>(); // added at runtime by the dashboard
        }
        bool show = map != null && map.Ready && map.IsMap && !map.Switching && Europe && !GuidedTour.InputLocked;
        if (show && toggle != null && toggle.WasPressedThisFrame()) Toggle();
        if (canvas.gameObject.activeSelf != show)
        {
            canvas.gameObject.SetActive(show);
            if (show) Place(true);
        }
        if (!show) return;
        UIKit.SetButtonLabel(button, map.IsDome ? "Flat map" : "Dome");
        Place(false);
    }

    /// <summary>Low on the left of the view, turned with the head around the vertical, following it smoothly.</summary>
    private void Place(bool snap)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Quaternion facing = Quaternion.LookRotation(UIKit.FlatForward(head), Vector3.up);
        Vector3 target = head.position + facing * offset;
        Transform t = canvas.transform;
        t.position = snap ? target : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-follow * Time.deltaTime));
        t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);
    }
}
