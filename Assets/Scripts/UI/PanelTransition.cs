using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Panels arrive and leave as a material instead of popping: they grow from 96% and fade in,
/// starting a little toward where they were called from (the dashboard from the left hand,
/// the info card from its airport), and leave the same way. A critically damped spring drives
/// it, so reopening a panel that is closing turns it around without a jump. The panel's
/// children are moved under one "Body" rect that is scaled and offset; the canvas itself
/// (placement, size, scale) stays the panel's own. A closing panel stops taking input at once
/// and switches its object off when it has faded. Reduce motion (Comfort): fade only.
/// Added by the panels themselves (PanelTransition.On(canvas)).
/// </summary>
[DisallowMultipleComponent]
public class PanelTransition : MonoBehaviour
{
    [Tooltip("Spring smooth time (s); the panel settles in about three times this.")]
    public float smoothTime = 0.06f;
    [Tooltip("Size the panel grows from and shrinks to.")]
    public float startScale = 0.96f;
    [Tooltip("Largest distance (m) the panel starts from, toward where it was called.")]
    public float maxSourceOffset = 0.08f;

    private RectTransform root;
    private RectTransform body;
    private CanvasGroup group;
    private float progress;
    private float velocity;
    private bool shown;
    private Vector3 offset; // canvas units, at progress 0

    private static NearFarInteractor[] interactors;
    private static Vector3 lastPress;
    private static float lastPressTime = -10f;

    /// <summary>True from Show until Hide, also while it is still fading in or out.</summary>
    public bool Shown => shown;

    /// <summary>0 hidden .. 1 fully shown.</summary>
    public float Progress => progress;

    /// <summary>The transition of this canvas, added on first use.</summary>
    public static PanelTransition On(Canvas canvas)
    {
        PanelTransition t = canvas.GetComponent<PanelTransition>();
        if (t == null) t = canvas.gameObject.AddComponent<PanelTransition>();
        t.Init();
        return t;
    }

    /// <summary>Called by UIKit buttons on press, so a panel they open grows from them.</summary>
    public static void NotePress(Vector3 position)
    {
        lastPress = position;
        lastPressTime = Time.unscaledTime;
    }

    /// <summary>The button pressed in the last half second (it opened the panel), else `fallback`.</summary>
    public static Vector3? CalledFrom(Vector3? fallback = null)
    {
        return Time.unscaledTime - lastPressTime < 0.5f ? lastPress : fallback;
    }

    /// <summary>Where the hand of that side is (its Near-Far interactor), or null without one.</summary>
    public static Vector3? HandPosition(InteractorHandedness hand)
    {
        if (interactors == null || interactors.Length == 0)
        {
            interactors = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor != null && interactor.isActiveAndEnabled && interactor.handedness == hand) return interactor.transform.position;
        }
        return null;
    }

    private void Init()
    {
        if (root != null) return;
        root = (RectTransform)transform;
        group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();
        body = UIKit.NewRect("Body", root);
        UIKit.Stretch(body);
        body.pivot = root.pivot; // grows from the same point as the panel (the info card: its bottom edge)
        WrapChildren();
        shown = gameObject.activeSelf;
        progress = shown ? 1f : 0f;
        Apply();
    }

    // Everything on the canvas goes under Body, in its order, so it scales and moves together.
    private void WrapChildren()
    {
        var children = new List<Transform>();
        foreach (Transform child in root) if (child != body) children.Add(child);
        foreach (Transform child in children) child.SetParent(body, false);
    }

    /// <summary>Shows the panel, growing from `source` (world position) when given.</summary>
    public void Show(Vector3? source = null)
    {
        Init();
        if (!gameObject.activeSelf)
        {
            progress = 0f;
            velocity = 0f;
            gameObject.SetActive(true);
        }
        shown = true;
        SetSource(source);
        group.blocksRaycasts = true;
        group.interactable = true;
        Apply();
    }

    /// <summary>Fades the panel out toward `toward` (world position), else back toward where it came from; its object goes off when faded.</summary>
    public void Hide(Vector3? toward = null)
    {
        Init();
        shown = false;
        group.blocksRaycasts = false;
        group.interactable = false;
        if (!gameObject.activeSelf) return;
        if (toward.HasValue) SetSource(toward);
    }

    /// <summary>Hides at once, without fading.</summary>
    public void HideNow()
    {
        Init();
        shown = false;
        progress = 0f;
        velocity = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;
        Apply();
        gameObject.SetActive(false);
    }

    private void SetSource(Vector3? world)
    {
        offset = Vector3.zero;
        if (!world.HasValue) return;
        Vector3 toSource = Vector3.ClampMagnitude(world.Value - root.position, maxSourceOffset);
        offset = root.InverseTransformVector(toSource);
        offset.z = 0f; // stays in the panel's plane
    }

    private void Update()
    {
        if (root == null) return;
        if (root.childCount > 1) WrapChildren(); // something was added to the canvas itself later
        float target = shown ? 1f : 0f;
        progress = Mathf.SmoothDamp(progress, target, ref velocity, Mathf.Max(0.001f, smoothTime), Mathf.Infinity, Time.unscaledDeltaTime);
        if (Mathf.Abs(progress - target) < 0.002f && Mathf.Abs(velocity) < 0.02f)
        {
            progress = target;
            velocity = 0f;
        }
        Apply();
        if (!shown && progress <= 0f) gameObject.SetActive(false);
    }

    private void Apply()
    {
        float p = Mathf.Clamp01(progress);
        group.alpha = p;
        bool still = Comfort.ReduceMotion;
        float s = still ? 1f : Mathf.LerpUnclamped(startScale, 1f, p);
        body.localScale = new Vector3(s, s, 1f);
        body.anchoredPosition3D = still ? Vector3.zero : offset * (1f - p);
    }
}
