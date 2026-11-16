using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Button for the dome (GeoMapView.SetDome): while the map view is shown, a small "Dome" button
/// floats low on the left of the view; it bends the map of Europe around you, and "Flat map"
/// brings the flat map back (B / the dashboard always open the flat map). Only for the
/// EUROCONTROL graph (*_ectrl files): the other datasets are worldwide. G does the same in the
/// Editor. It never covers another panel: hidden while the dashboard is open, and docked just
/// above the left end of the timeline / Live flights bar while one of them is open (the head
/// offset below sits inside the bars' footprint). Self-contained: it adds itself when the scene
/// has a GraphLoader.
/// </summary>
// After the dashboard and the bars (DefaultExecutionOrder 100), so it docks to this frame's bar.
[DefaultExecutionOrder(110)]
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
    [Tooltip("Gap (m) between the button and the top edge of the timeline / Live flights bar it docks to.")]
    public float dockGap = 0.015f;
    [Tooltip("Beyond this distance from its spot (m), e.g. after a teleport, the button jumps instead of gliding.")]
    public float snapDistance = 0.5f;

    private Canvas canvas;
    private Button button;
    private InputAction toggle;
    private float nextLookup;
    private float nextPanelLookup;
    private DataDashboard dashboard;
    private FlightPanel flights;
    private TimelinePanel timeline;

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
        if ((dashboard == null || flights == null || timeline == null) && Time.unscaledTime >= nextPanelLookup)
        {
            nextPanelLookup = Time.unscaledTime + 1f;
            if (dashboard == null) dashboard = FindFirstObjectByType<DataDashboard>();
            if (flights == null) flights = FindFirstObjectByType<FlightPanel>();
            if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
        }
        // Shown also mid-way through flat <-> dome (so it can be turned around); hidden over the dashboard.
        bool show = map != null && map.Ready && map.IsMap && (!map.Switching || map.ChangingShape) && Europe &&
                    !GuidedTour.InputLocked && (dashboard == null || !dashboard.IsOpen);
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

    /// <summary>The open timeline / Live flights bar, or null.</summary>
    private RectTransform OpenBar()
    {
        if (flights != null && flights.IsOpen && flights.Panel != null) return flights.Panel;
        if (timeline != null && timeline.IsOpen && timeline.Panel != null) return timeline.Panel;
        return null;
    }

    /// <summary>
    /// A bar open: just above its left end, same angle. Else low on the left of the view, turned
    /// with the head around the vertical, following it smoothly.
    /// </summary>
    private void Place(bool snap)
    {
        RectTransform bar = OpenBar();
        if (bar != null)
        {
            var self = (RectTransform)canvas.transform;
            float barHalfW = bar.rect.width * 0.5f * bar.lossyScale.x;
            float barHalfH = bar.rect.height * 0.5f * bar.lossyScale.y;
            float halfW = self.rect.width * 0.5f * self.lossyScale.x;
            float halfH = self.rect.height * 0.5f * self.lossyScale.y;
            self.rotation = bar.rotation;
            self.position = bar.position + bar.up * (barHalfH + dockGap + halfH) - bar.right * (barHalfW - halfW);
            return;
        }
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Quaternion facing = Quaternion.LookRotation(UIKit.FlatForward(head), Vector3.up);
        Vector3 target = head.position + facing * offset;
        Transform t = canvas.transform;
        // Far off (a teleport: R3, or entering the dome recentres you; or just undocked from a bar): jump.
        if (Vector3.Distance(t.position, target) > snapDistance) snap = true;
        t.position = snap ? target : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-follow * Time.deltaTime));
        t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);
    }
}
