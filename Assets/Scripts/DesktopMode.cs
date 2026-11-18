using System;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Desktop condition of the immersion study (StudyCondition): the same scene, data,
/// features and look as in the headset, on a monitor with mouse and keyboard. Adds itself at
/// scene load when the condition is Desktop, then:
/// - stops XR (XR Plug-in Management, by reflection so the scripts compile without it);
/// - keeps the XR rig's own camera, so rendering is identical, but switches off its head
///   tracking and the rig's controllers, hands and locomotion (the wrist buttons hide with them);
/// - starts the eyes where a standing participant's would be at the rig, facing the same way;
/// - adds DesktopLook (look around from inside the graph, move, double-click to go to an
///   airport), turns on mouse selection (GraphSelector) and switches off the XR-only input
///   (XRGraphInput, HandTeleport, FlyCamera);
/// - shows Menu and Home buttons in the lower left corner, the desktop counterpart of the
///   wrist buttons (hidden on the start screen and during the tour, like them).
/// Panels, voice (hold N), map, timeline, live flights, tour and the room's walls work as in VR.
/// </summary>
public class DesktopMode : MonoBehaviour
{
    [Tooltip("Vertical field of view of the monitor view (degrees).")]
    public float fieldOfView = 60f;
    [Tooltip("Eye height (m) above the XR rig at the start, like a standing participant in the headset.")]
    public float eyeHeight = 1.6f;

    public static DesktopMode Instance { get; private set; }
    /// <summary>The camera shown on the monitor (the XR rig's camera when there is one).</summary>
    public Camera ViewCamera { get; private set; }
    public DesktopLook Look { get; private set; }

    private Canvas hud;
    private DataDashboard dashboard;
    private StartPanel startPanel;
    private float nextLookup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (!StudyCondition.IsDesktop || FindFirstObjectByType<DesktopMode>() != null) return;
        new GameObject("Desktop Mode").AddComponent<DesktopMode>();
    }

    private void Awake()
    {
        Instance = this;
        StopXR();
        SetUpCamera();
        SetUpInput();
        BuildHud();
        Debug.Log($"DesktopMode: monitor view with '{ViewCamera.name}', fov {fieldOfView:F0}");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (hud != null) Destroy(hud.gameObject);
    }

    // ---- Setup -----------------------------------------------------------

    private static void StopXR()
    {
        // XRGeneralSettings.Instance.Manager: stop the headset if "Initialize XR on Startup" started it.
        Type settingsType = Type.GetType("UnityEngine.XR.Management.XRGeneralSettings, Unity.XR.Management");
        object settings = settingsType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        object manager = settings?.GetType().GetProperty("Manager")?.GetValue(settings);
        if (manager == null)
        {
            Debug.Log("DesktopMode: no XR Plug-in Management; nothing to stop");
            return;
        }
        bool running = manager.GetType().GetProperty("isInitializationComplete")?.GetValue(manager) is bool b && b;
        if (!running)
        {
            Debug.Log("DesktopMode: XR not running");
            return;
        }
        manager.GetType().GetMethod("StopSubsystems")?.Invoke(manager, null);
        manager.GetType().GetMethod("DeinitializeLoader")?.Invoke(manager, null);
        Debug.Log("DesktopMode: XR stopped");
    }

    private void SetUpCamera()
    {
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = origin != null && origin.Camera != null ? origin.Camera : Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Desktop Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            cam = go.GetComponent<Camera>();
        }
        if (origin != null) KeepOnlyCameraPath(origin.transform, cam.transform);
        // Head tracking would hold the camera at the headset's last pose.
        foreach (Behaviour b in cam.GetComponents<Behaviour>())
        {
            if (b.GetType().Name.Contains("TrackedPoseDriver")) b.enabled = false;
        }
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.fieldOfView = fieldOfView;
        if (origin != null)
        {
            cam.transform.SetPositionAndRotation(origin.transform.position + Vector3.up * eyeHeight,
                                                 Quaternion.Euler(0f, origin.transform.eulerAngles.y, 0f));
        }
        foreach (FlyCamera fly in FindObjectsByType<FlyCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None)) fly.enabled = false;
        Look = cam.GetComponent<DesktopLook>();
        if (Look == null) Look = cam.gameObject.AddComponent<DesktopLook>();
        ViewCamera = cam;
    }

    // Everything of the rig that is not on the way from its root to the camera goes off:
    // controllers, hands, their interactors and the wrist buttons that follow them, locomotion.
    private static void KeepOnlyCameraPath(Transform root, Transform camera)
    {
        for (Transform t = camera; t != null && t != root; t = t.parent)
        {
            Transform parent = t.parent;
            if (parent == null) break;
            foreach (Transform sibling in parent)
            {
                if (sibling != t && sibling.gameObject.activeSelf) sibling.gameObject.SetActive(false);
            }
        }
    }

    private static void SetUpInput()
    {
        foreach (XRGraphInput x in FindObjectsByType<XRGraphInput>(FindObjectsInactive.Include, FindObjectsSortMode.None)) x.enabled = false;
        foreach (HandTeleport h in FindObjectsByType<HandTeleport>(FindObjectsInactive.Include, FindObjectsSortMode.None)) h.enabled = false;
        foreach (GraphSelector s in FindObjectsByType<GraphSelector>(FindObjectsInactive.Include, FindObjectsSortMode.None)) s.mouseInput = true;
    }

    // ---- Menu / Home buttons (the wrist buttons of the headset) ----------------

    private void BuildHud()
    {
        UIKit.EnsureXREventSystem();
        var go = new GameObject("Desktop Buttons", typeof(RectTransform));
        hud = go.AddComponent<Canvas>();
        // Screen Space - Camera: drawn by the view camera, so screenshots include it.
        hud.renderMode = RenderMode.ScreenSpaceCamera;
        hud.worldCamera = ViewCamera;
        hud.planeDistance = ViewCamera.nearClipPlane + 0.02f;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;
        go.AddComponent<GraphicRaycaster>();

        RectTransform bar = UIKit.NewRect("Buttons", go.transform);
        bar.anchorMin = bar.anchorMax = bar.pivot = Vector2.zero;
        bar.anchoredPosition = new Vector2(28f, 28f);
        bar.sizeDelta = new Vector2(372f, 64f);
        UIKit.Button(bar, "Menu", ToggleDashboard, 0f, 0f, 180f, 64f, UIKit.ButtonStyle.Secondary, UIKit.BodySize, UIIcon.Shape.Grid);
        UIKit.Button(bar, "Home", OpenHome, 192f, 0f, 180f, 64f, UIKit.ButtonStyle.Secondary, UIKit.BodySize);
        hud.gameObject.SetActive(false);
    }

    private void ToggleDashboard()
    {
        Find();
        if (dashboard == null) return;
        if (dashboard.IsOpen) dashboard.Close();
        else dashboard.Open();
    }

    private void OpenHome()
    {
        Find();
        if (startPanel != null) startPanel.Open();
    }

    private void Find()
    {
        if (dashboard == null) dashboard = FindFirstObjectByType<DataDashboard>();
        if (startPanel == null) startPanel = FindFirstObjectByType<StartPanel>();
    }

    private void LateUpdate()
    {
        if (hud == null) return;
        if (Time.unscaledTime >= nextLookup)
        {
            nextLookup = Time.unscaledTime + 1f;
            Find();
        }
        // Like the wrist buttons: not on the start screen, not during the tour.
        bool show = !GuidedTour.InputLocked && (startPanel == null || !startPanel.IsOpen);
        if (hud.gameObject.activeSelf != show) hud.gameObject.SetActive(show);
    }
}
