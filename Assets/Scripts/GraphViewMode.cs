using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

/// <summary>
/// Switches the graph between two views:
/// - TopRoutes: nodes coloured by traffic, the most important edges, labels on the
///   biggest airports (the scene's usual look);
/// - Regional: nodes and edges coloured by community (layout_3d.py `community`),
///   edges inside communities plus a few faint ones between them, labels on the
///   biggest airports of each community, the info panel names the cluster.
/// Toggle: B / Y on the controllers, left palm facing up + pinch with bare hands,
/// V in the Editor (XR Interaction Simulator: key 2 = secondary button).
/// A short notice in front of the viewer shows the new mode.
/// </summary>
public class GraphViewMode : MonoBehaviour
{
    public enum ViewMode { TopRoutes, Regional }

    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Optional; relabelled per mode. Found in the scene if unset.")]
    public HubLabels hubLabels;
    public ViewMode startMode = ViewMode.TopRoutes;

    [Header("Hand gesture (left palm up + pinch)")]
    public bool handGesture = true;
    [Tooltip("How closely the palm must face up: dot(palm direction, up).")]
    [Range(0f, 1f)] public float palmUpThreshold = 0.7f;
    [Tooltip("Thumb tip to index tip distance (m) that counts as a pinch.")]
    public float pinchDistance = 0.02f;

    [Header("Notice")]
    public float noticeSeconds = 1.5f;
    public float noticeDistance = 0.9f;
    public float noticeFontSize = 0.25f;

    public ViewMode Mode { get; private set; }

    private InputAction toggleAction;
    private XRHandSubsystem hands;
    private bool wasPinching;
    private bool applied;
    private TextMeshPro notice;
    private float noticeUntil;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (hubLabels == null) hubLabels = FindFirstObjectByType<HubLabels>();

        toggleAction = new InputAction("Toggle View Mode", InputActionType.Button);
        toggleAction.AddBinding("<XRController>{RightHand}/secondaryButton");
        toggleAction.AddBinding("<XRController>{LeftHand}/secondaryButton");
        toggleAction.AddBinding("<Keyboard>/v");

        notice = new GameObject("ViewMode Notice").AddComponent<TextMeshPro>();
        notice.fontSize = noticeFontSize;
        notice.alignment = TextAlignmentOptions.Center;
        notice.textWrappingMode = TextWrappingModes.NoWrap;
        notice.gameObject.SetActive(false);
    }

    private void OnEnable() => toggleAction.Enable();

    private void OnDisable() => toggleAction.Disable();

    private void OnDestroy()
    {
        toggleAction.Dispose();
        if (notice != null) Destroy(notice.gameObject);
    }

    private void Update()
    {
        if (graph == null || !graph.IsLoaded) return;
        if (!applied)
        {
            // First frame after loading: apply the start mode without a notice.
            Apply(startMode, false);
            applied = true;
        }

        if (toggleAction.WasPressedThisFrame() || LeftPalmUpPinchStarted())
        {
            Apply(Mode == ViewMode.TopRoutes ? ViewMode.Regional : ViewMode.TopRoutes, true);
        }

        if (notice.gameObject.activeSelf && Time.time > noticeUntil)
        {
            notice.gameObject.SetActive(false);
        }
    }

    public void Apply(ViewMode mode, bool showNotice)
    {
        Mode = mode;
        bool regional = mode == ViewMode.Regional;
        // Highlights are drawn on top of the old colours: drop them before recolouring.
        if (selector != null) selector.ClearSelection();
        graph.SetRegionalView(regional);
        if (hubLabels != null) hubLabels.SetRegional(regional);
        if (showNotice) ShowNotice(regional ? $"Regional clusters ({graph.Communities.Count})" : "Top routes");
    }

    /// <summary>True on the frame the left hand starts pinching while its palm faces up.</summary>
    private bool LeftPalmUpPinchStarted()
    {
        if (!handGesture) return false;
        if (hands == null || !hands.running)
        {
            hands = null;
            var list = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            foreach (XRHandSubsystem s in list)
            {
                if (s.running) hands = s;
            }
            if (hands == null) return false;
        }

        XRHand hand = hands.leftHand;
        bool pinching = false;
        if (hand.isTracked &&
            hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palm) &&
            hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumb) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose index))
        {
            // XR Hands' palm direction is the palm joint's -Y (as in its gesture utilities).
            bool palmUp = Vector3.Dot(palm.rotation * Vector3.down, Vector3.up) >= palmUpThreshold;
            pinching = palmUp && Vector3.Distance(thumb.position, index.position) <= pinchDistance;
        }
        bool started = pinching && !wasPinching;
        wasPinching = pinching;
        return started;
    }

    private void ShowNotice(string text)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        notice.text = "Mode: " + text;
        notice.transform.position = head.position + head.forward * noticeDistance;
        // TMP text reads correctly when its +Z points away from the viewer.
        notice.transform.rotation = Quaternion.LookRotation(notice.transform.position - head.position, Vector3.up);
        notice.gameObject.SetActive(true);
        noticeUntil = Time.time + noticeSeconds;
    }
}
