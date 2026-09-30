using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

/// <summary>
/// Teleport without a thumbstick, so it also works with bare hands: point at the
/// floor (or any Teleportation Area / Anchor), pinch (grip on controllers) and hold
/// for holdSeconds, a ray and a ring show where you will land; release to teleport.
/// Only starts when the select began on empty space, so pinching nodes is unaffected;
/// a short pinch on empty space stays a tap for XRGraphInput (edge pick / clear).
/// Uses the rig's TeleportationProvider, like XRI's own teleport.
/// </summary>
public class HandTeleport : MonoBehaviour
{
    [Tooltip("Found in the scene if unset (the XR Origin's Locomotion > Teleportation).")]
    public TeleportationProvider teleportationProvider;
    [Tooltip("Hold select this long on empty space to start aiming. Keep above XRGraphInput.tapMaxSeconds.")]
    public float holdSeconds = 0.45f;
    public float maxDistance = 30f;

    [Header("Visuals")]
    public Color validColor = new Color(0.2f, 0.9f, 1f, 0.9f);
    public Color invalidColor = new Color(1f, 0.3f, 0.3f, 0.6f);
    public float rayWidth = 0.008f;
    public float ringDiameter = 0.4f;
    [Tooltip("Optional material with vertex colours / alpha (e.g. Sprites/Default). If unset, Sprites/Default is created.")]
    public Material visualMaterial;

    private class Aim
    {
        public float pressTime;
        public bool eligible;
        public bool aiming;
        public bool hasTarget;
        public Vector3 target;
    }

    private NearFarInteractor[] interactors = new NearFarInteractor[0];
    private readonly Dictionary<NearFarInteractor, Aim> aims = new Dictionary<NearFarInteractor, Aim>();
    private LineRenderer ray;
    private Transform ring;
    private Material ownedMaterial;
    private Material ringMaterial;

    private void Awake()
    {
        if (teleportationProvider == null) teleportationProvider = FindFirstObjectByType<TeleportationProvider>();
        BuildVisuals();
    }

    private void Start()
    {
        // Include inactive: the hands rig switches between hand and controller interactors at runtime.
        interactors = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (teleportationProvider == null)
        {
            Debug.LogWarning("HandTeleport: no TeleportationProvider in scene; hand teleport disabled.");
        }
    }

    private void OnDestroy()
    {
        if (ownedMaterial != null) Destroy(ownedMaterial);
        if (ringMaterial != null) Destroy(ringMaterial);
        if (ray != null) Destroy(ray.gameObject);
        if (ring != null) Destroy(ring.gameObject);
    }

    private void Update()
    {
        if (GuidedTour.InputLocked)
        {
            // No teleports during the guided tour; forget any aim in progress.
            foreach (Aim aim in aims.Values)
            {
                aim.eligible = false;
                aim.aiming = false;
            }
            HideVisuals();
            return;
        }
        NearFarInteractor shown = null;
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled) continue;
            if (!aims.TryGetValue(interactor, out Aim aim))
            {
                aim = new Aim();
                aims.Add(interactor, aim);
            }

            if (interactor.selectInput.ReadWasPerformedThisFrame())
            {
                aim.pressTime = Time.time;
                // Not over nodes or UI (e.g. the dashboard): only empty space starts a teleport.
                aim.eligible = !interactor.hasHover && !interactor.hasSelection &&
                               !interactor.TryGetCurrentUIRaycastResult(out _);
                aim.aiming = false;
                aim.hasTarget = false;
            }

            if (aim.eligible && interactor.selectInput.ReadIsPerformed())
            {
                if (!aim.aiming && Time.time - aim.pressTime >= holdSeconds) aim.aiming = true;
                if (aim.aiming)
                {
                    aim.hasTarget = FindTarget(interactor, out aim.target, out Vector3 rayEnd);
                    if (shown == null)
                    {
                        shown = interactor;
                        ShowVisuals(interactor.curveOrigin.position, rayEnd, aim.hasTarget);
                    }
                }
            }

            if (interactor.selectInput.ReadWasCompletedThisFrame())
            {
                if (aim.aiming && aim.hasTarget && teleportationProvider != null)
                {
                    teleportationProvider.QueueTeleportRequest(new TeleportRequest
                    {
                        destinationPosition = aim.target,
                        destinationRotation = Quaternion.identity,
                        requestTime = Time.time,
                        matchOrientation = MatchOrientation.None,
                    });
                }
                aim.eligible = false;
                aim.aiming = false;
                aim.hasTarget = false;
            }
        }

        if (shown == null) HideVisuals();
    }

    /// <summary>True if the hand's ray hits a Teleportation Area / Anchor; rayEnd is where the ray stops either way.</summary>
    private bool FindTarget(NearFarInteractor interactor, out Vector3 target, out Vector3 rayEnd)
    {
        Transform origin = interactor.curveOrigin;
        target = Vector3.zero;
        rayEnd = origin.position + origin.forward * maxDistance;
        if (!Physics.Raycast(origin.position, origin.forward, out RaycastHit hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }
        rayEnd = hit.point;
        if (hit.collider.GetComponentInParent<BaseTeleportationInteractable>() == null)
        {
            return false; // e.g. a node or a wall
        }
        target = hit.point;
        return true;
    }

    private void BuildVisuals()
    {
        Material material = visualMaterial;
        if (material == null)
        {
            ownedMaterial = new Material(Shader.Find("Sprites/Default"));
            material = ownedMaterial;
        }

        ray = new GameObject("HandTeleport Ray").AddComponent<LineRenderer>();
        ray.useWorldSpace = true;
        ray.positionCount = 2;
        ray.startWidth = rayWidth;
        ray.endWidth = rayWidth;
        ray.sharedMaterial = material;
        ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ray.receiveShadows = false;

        GameObject ringGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ringGo.name = "HandTeleport Target";
        // The marker must never block rays (its own, node selection or other teleports).
        Destroy(ringGo.GetComponent<Collider>());
        ring = ringGo.transform;
        ring.localScale = new Vector3(ringDiameter, 0.005f, ringDiameter);
        // The ring is only shown on a valid target, so it gets its own material in validColor
        // (the cylinder has no vertex colours to tint through).
        ringMaterial = new Material(material) { color = validColor };
        Renderer ringRenderer = ringGo.GetComponent<Renderer>();
        ringRenderer.sharedMaterial = ringMaterial;
        ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;

        HideVisuals();
    }

    private void ShowVisuals(Vector3 from, Vector3 to, bool valid)
    {
        Color color = valid ? validColor : invalidColor;
        ray.gameObject.SetActive(true);
        ray.SetPosition(0, from);
        ray.SetPosition(1, to);
        ray.startColor = color;
        ray.endColor = color;

        ring.gameObject.SetActive(valid);
        if (valid) ring.position = to + Vector3.up * 0.01f;
    }

    private void HideVisuals()
    {
        if (ray != null) ray.gameObject.SetActive(false);
        if (ring != null) ring.gameObject.SetActive(false);
    }
}
