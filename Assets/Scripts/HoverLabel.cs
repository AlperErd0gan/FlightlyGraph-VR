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
/// collider, so it never blocks selection; right on top of the airport (its HubLabels code is
/// hidden meanwhile), its height in proportion to the airport (sizePerNode, at least
/// minSizePerMetre of the distance). In 3D it faces the eyes; on the map it lies on the map
/// surface like the map's own names, just north of the airport. Not shown for the selected airport (the info card names it). Optional: untick showOnHover. Self-contained: it adds itself when the scene has
/// a GraphLoader and changes no other script.
/// </summary>
public class HoverLabel : MonoBehaviour
{
    public GraphLoader graph;
    public GraphSelector selector;
    [Tooltip("Show the airport name while a ray / the mouse points at it.")]
    public bool showOnHover = true;
    [Tooltip("Text height relative to the airport's diameter (0.6 = 60 % of it), in 3D and on the map.")]
    public float sizePerNode = 0.6f;
    [Tooltip("Smallest text height as a fraction of its distance from the eyes (0.012 = 1.2 cm at 1 m), so small airports stay readable.")]
    public float minSizePerMetre = 0.012f;
    [Tooltip("Seconds the name stays after the ray leaves (no flicker between neighbouring airports).")]
    public float holdSeconds = 0.15f;
    public Color color = new Color(1f, 1f, 1f, 0.95f);

    // Trailing "(IST)" / "(LTFM)": HubLabels already shows the code.
    private static readonly Regex CodeSuffix = new Regex(@"\s*\([A-Z0-9]{3,4}\)\s*$");

    private TextMeshPro text;
    private GraphNode shown;
    private TextMeshPro hiddenCode;   // the shown airport's HubLabels code, hidden while hovered
    private readonly Dictionary<string, TextMeshPro> codes = new Dictionary<string, TextMeshPro>();
    private GeoMapView map;
    private float nextMapLookup;
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
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 1f; // 0.1 m line height at scale 1
        text.rectTransform.sizeDelta = new Vector2(8f, 0.12f);
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.outlineWidth = 0.2f;
        text.outlineColor = new Color32(0, 0, 0, 200);
        go.SetActive(false);
    }

    private void OnDisable() => Hide(); // never leave an airport code hidden

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
                ShowCode();
                shown = node;
                text.text = CodeSuffix.Replace(node.label ?? node.id, "");
                HideCode(node);
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
        ShowCode();
        shown = null;
        if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
    }

    /// <summary>
    /// Right on top of the airport. Map view: on the map surface (lifted a little), just north of
    /// the airport, oriented like the map's names. 3D: above it, facing the eyes. Height in
    /// proportion to the airport (see TextHeight).
    /// </summary>
    private void Place(GraphNode node)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        Transform t = text.transform;
        float radius = node.transform.lossyScale.y * 0.5f;
        float lineHeight = TextHeight(node, cam);
        t.localScale = Vector3.one * (lineHeight / 0.1f);

        if (OnMap(out GeoMapProjection projection))
        {
            // The projection works in the graph's local space (as GeoMapView places it).
            Transform g = graph.transform;
            Vector2 p = projection.Unproject(g.InverseTransformPoint(node.transform.position));
            Vector3 normal = g.TransformDirection(projection.NormalAt(p)).normalized;
            Vector3 north = g.TransformDirection(projection.NorthAt(p)).normalized;
            t.position = node.transform.position + north * (radius + lineHeight * 0.6f) + normal * 0.004f;
            // TMP text reads correctly when its +Z points away from the viewer: into the map.
            t.rotation = Quaternion.LookRotation(-normal, north);
            return;
        }

        t.position = node.transform.position + cam.transform.up * (radius + lineHeight * 0.6f);
        t.rotation = Quaternion.LookRotation(t.position - cam.transform.position, cam.transform.up);
    }

    /// <summary>Line height: a share of the airport's diameter, but not smaller than readable at its distance.</summary>
    private float TextHeight(GraphNode node, Camera cam)
    {
        float diameter = node.transform.lossyScale.y;
        float distance = Vector3.Distance(cam.transform.position, node.transform.position);
        return Mathf.Max(diameter * sizePerNode, distance * minSizePerMetre, 0.004f);
    }

    /// <summary>True while the map view is fully shown (not during the fly-over between layouts).</summary>
    private bool OnMap(out GeoMapProjection projection)
    {
        projection = null;
        if (map == null && Time.unscaledTime >= nextMapLookup)
        {
            nextMapLookup = Time.unscaledTime + 2f;
            map = FindFirstObjectByType<GeoMapView>();
        }
        if (map == null || !map.Ready || !map.IsMap || map.Switching || map.Projection == null) return false;
        projection = map.Projection;
        return true;
    }

    /// <summary>Hides the airport's code from HubLabels ("Label &lt;id&gt;") while its name is shown on top.</summary>
    private void HideCode(GraphNode node)
    {
        if (!codes.TryGetValue(node.id, out hiddenCode) || hiddenCode == null)
        {
            // Index HubLabels' codes once (and again if it was rebuilt), instead of a scene search per hover.
            codes.Clear();
            GameObject root = GameObject.Find("HubLabels");
            if (root != null)
            {
                foreach (TextMeshPro code in root.GetComponentsInChildren<TextMeshPro>(true))
                {
                    string name = code.gameObject.name;
                    if (name.StartsWith("Label ")) codes[name.Substring(6)] = code;
                }
            }
            codes.TryGetValue(node.id, out hiddenCode);
        }
        // HubLabels toggles the object, not the text component, so this stays until ShowCode.
        if (hiddenCode != null) hiddenCode.enabled = false;
    }

    private void ShowCode()
    {
        if (hiddenCode != null) hiddenCode.enabled = true;
        hiddenCode = null;
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
