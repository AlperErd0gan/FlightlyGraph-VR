using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Airport name on hover: pointing a controller ray (or hand ray) at an airport shows its name
/// above it, without selecting it or pressing anything; it fades away shortly after the ray
/// leaves. In the Editor without a headset the mouse does the same. One reused text, no
/// collider, so it never blocks selection; placed above the airport code (HubLabels) and kept
/// the same apparent size near and far. Not shown for the selected airport (the info card
/// names it). Optional: untick showOnHover. Self-contained: it adds itself when the scene has
/// a GraphLoader and changes no other script.
/// </summary>
public class HoverLabel : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Show the airport name while a ray / the mouse points at it.")]
    public bool showOnHover = true;
    [Tooltip("Text height as a fraction of its distance from the eyes (0.03 = 3 cm at 1 m).")]
    public float sizePerMetre = 0.03f;
    [Tooltip("Seconds the name stays after the ray leaves (no flicker between neighbouring airports).")]
    public float holdSeconds = 0.15f;
    public Color color = new Color(1f, 1f, 1f, 0.95f);

    // Trailing "(IST)" / "(LTFM)": HubLabels already shows the code.
    private static readonly Regex CodeSuffix = new Regex(@"\s*\([A-Z0-9]{3,4}\)\s*$");

    private TextMeshPro text;
    private GraphNode shown;
    private float lastSeen;
    private readonly List<NearFarInteractor> interactors = new List<NearFarInteractor>();
    private float nextInteractorLookup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<HoverLabel>() != null) return;
        if (FindFirstObjectByType<GraphLoader>() == null) return;
        new GameObject("HoverLabel").AddComponent<HoverLabel>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        var go = new GameObject("Hover Label Text");
        go.transform.SetParent(transform, false);
        text = go.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Bottom;
        text.fontSize = 1f; // 0.1 m line height at scale 1
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.outlineWidth = 0.2f;
        text.outlineColor = new Color32(0, 0, 0, 200);
        go.SetActive(false);
    }

    private void LateUpdate()
    {
        if (graph == null || !graph.IsLoaded || !showOnHover)
        {
            Hide();
            return;
        }
        GraphNode node = Hovered();
        if (node != null && (selector == null || node != selector.SelectedNode) && node.gameObject.activeInHierarchy)
        {
            if (node != shown)
            {
                shown = node;
                text.text = CodeSuffix.Replace(node.label ?? node.id, "");
            }
            lastSeen = Time.unscaledTime;
        }
        if (shown == null || !shown.gameObject.activeInHierarchy || Time.unscaledTime - lastSeen > holdSeconds ||
            (selector != null && shown == selector.SelectedNode))
        {
            Hide();
            return;
        }
        Place(shown);
    }

    private void Hide()
    {
        shown = null;
        if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
    }

    /// <summary>Above the airport and its code, facing the eyes, the same apparent size at any distance.</summary>
    private void Place(GraphNode node)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        Transform t = text.transform;
        float distance = Vector3.Distance(cam.transform.position, node.transform.position);
        float lineHeight = Mathf.Max(0.005f, distance * sizePerMetre);
        t.localScale = Vector3.one * (lineHeight / 0.1f);
        // HubLabels puts the code just above the airport: go one more line above that.
        t.position = node.transform.position + cam.transform.up * (node.transform.lossyScale.y * 0.5f + lineHeight * 1.6f);
        t.rotation = Quaternion.LookRotation(t.position - cam.transform.position, cam.transform.up);
    }

    /// <summary>Airport under a controller / hand ray (right hand first), else under the mouse in the Editor.</summary>
    private GraphNode Hovered()
    {
        if (Time.unscaledTime >= nextInteractorLookup)
        {
            nextInteractorLookup = Time.unscaledTime + 1f;
            interactors.Clear();
            interactors.AddRange(FindObjectsByType<NearFarInteractor>(FindObjectsSortMode.None));
            // Right hand first.
            interactors.Sort((a, b) => (b.handedness == InteractorHandedness.Right).CompareTo(a.handedness == InteractorHandedness.Right));
        }
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled) continue;
            foreach (IXRHoverInteractable hovered in interactor.interactablesHovered)
            {
                GraphNode node = hovered.transform.GetComponent<GraphNode>();
                if (node != null) return node;
            }
        }
        return XRSettings.isDeviceActive ? null : MouseNode();
    }

    private static GraphNode MouseNode()
    {
        Mouse mouse = Mouse.current;
        Camera cam = Camera.main;
        if (mouse == null || cam == null) return null;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return null;
        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        return Physics.Raycast(ray, out RaycastHit hit, 500f) ? hit.collider.GetComponentInParent<GraphNode>() : null;
    }
}
