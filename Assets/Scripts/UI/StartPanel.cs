using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Simple start screen: when the app opens the graph is hidden and one panel floats in
/// front of the user with Start exploring, Guided tour and Controls. Start exploring
/// hides the panel and shows the graph with its intro animation (GraphIntro); Guided tour
/// does the same, then starts the GuidedTour once the intro has ended; Controls shows the
/// controls table (ControlsHelp) with a Back button. Afterwards a small Home button on the
/// left wrist (shown when the inside of the wrist faces you) brings the panel back; the
/// graph stays where it is. Above the panel floats a small hologram of the network (all
/// airports and the busiest routes of the loaded graph, in their colours): the 3D layout
/// turning slowly, morphing now and then onto a small map of Europe and back.
/// Only one panel at a time in front of you: opening the start
/// panel closes the dashboard and the timeline, and opening one of them (menu button,
/// voice) closes the start panel again (before the first start they stay closed instead).
/// Self-contained: it adds itself when the scene has a GraphLoader and changes no other script.
/// </summary>
public class StartPanel : MonoBehaviour
{
    public GraphLoader graph;
    public GraphIntro intro;
    public GuidedTour tour;
    public DataDashboard dashboard;
    public TimelinePanel timeline;
    [Tooltip("Show the panel (and hide the graph) when the app opens.")]
    public bool showOnStart = true;
    [Tooltip("Key that opens the panel (Input System path), e.g. for testing in the Editor. Empty = none.")]
    public string homeKey = "<Keyboard>/home";

    [Header("Panel")]
    [Tooltip("Distance (m) in front of the eyes.")]
    public float distance = 1.3f;
    [Tooltip("Height (m) of the panel's centre relative to the eyes.")]
    public float heightOffset = -0.3f;
    [Tooltip("The panel moves back in front of you when you look more than this far (deg) away from it.")]
    public float followAngle = 50f;
    public float followSpeed = 3f;

    [Header("Hologram")]
    [Tooltip("Miniature of the network floating above the panel.")]
    public bool hologram = true;
    [Tooltip("Size (m) of the hologram's largest side.")]
    public float hologramSize = 0.5f;
    [Tooltip("Busiest routes drawn in the hologram.")]
    public int hologramRoutes = 300;
    [Tooltip("Turning speed (deg/s).")]
    public float hologramSpin = 10f;
    [Tooltip("Morph the hologram onto the map of Europe and back (needs geomap.json).")]
    public bool hologramMap = true;
    [Tooltip("Seconds the hologram stays 3D / on the map, and seconds a morph takes.")]
    public float hologramHoldSeconds = 6f;
    public float hologramMorphSeconds = 2.5f;
    [Tooltip("Tilt (deg) of the map hologram: 0 = flat like a table, 90 = upright.")]
    public float hologramMapTilt = 45f;

    [Header("Wrist Home button")]
    public bool wristButton = true;
    [Tooltip("Inside of the left wrist in the left controller's / hand's space (x right, y up, z forward).")]
    public Vector3 palmAxis = Vector3.right;
    [Tooltip("Where the button sits in the left controller's / hand's space (m).")]
    public Vector3 wristOffset = new Vector3(0.03f, 0f, -0.1f);
    [Tooltip("How directly the inside of the wrist must face the eyes to show the button: dot(palm, to eyes).")]
    [Range(0f, 1f)] public float showThreshold = 0.55f;
    [Tooltip("Button diameter (m).")]
    public float wristButtonSize = 0.06f;

    /// <summary>True while the start panel is shown.</summary>
    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;

    private static readonly Vector2 Size = new Vector2(760f, 760f);
    private const float Pad = 48f;

    private Canvas canvas;
    private RectTransform homePage;
    private RectTransform controlsPage;
    private Button startButton;
    private Canvas wrist;
    private NearFarInteractor leftHand;
    private float nextHandSearch;
    private InputAction homeAction;
    private Transform holo;
    private Material holoMaterial;
    private readonly List<Mesh> holoMeshes = new List<Mesh>();
    private float openedAt;
    private bool holoBuilding;
    private bool hasMapShape;
    private const int ArcSamples = 16;
    private Mesh routeMesh;
    private Mesh airportMesh;
    private Vector3[] shellLine, mapLine, lineNow;
    private Vector3[] shellNode, mapNode, nodeNow;
    private float[] nodeRadius;
    private Color[] nodeColor;
    private int[] routeEnds;
    private Vector3[] ico;
    private float shownShape = -1f;
    private float holoSpin;
    private Transform holoDisc;
    private Material holoDiscMaterial;

    private bool started;       // the user has left the start panel once
    private bool graphHidden;
    private bool tourPending;
    private Transform nodes;
    private GameObject labels;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<StartPanel>() != null) return;
        if (FindFirstObjectByType<GraphLoader>() == null) return;
        // Own object, so hiding the graph never touches this component.
        new GameObject("StartPanel").AddComponent<StartPanel>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (!string.IsNullOrEmpty(homeKey)) homeAction = new InputAction("Start Panel Home", InputActionType.Button, homeKey);
        UIKit.EnsureXREventSystem();
        Build();
        BuildWrist();
        started = !showOnStart;
        if (showOnStart) Open();
        else canvas.gameObject.SetActive(false);
    }

    private void OnEnable() => homeAction?.Enable();

    private void OnDisable() => homeAction?.Disable();

    private void OnDestroy()
    {
        homeAction?.Dispose();
        if (canvas != null) Destroy(canvas.gameObject);
        if (wrist != null) Destroy(wrist.gameObject);
        if (holo != null) Destroy(holo.gameObject);
        if (holoMaterial != null) Destroy(holoMaterial);
        if (holoDiscMaterial != null) Destroy(holoDiscMaterial);
        foreach (Mesh m in holoMeshes) if (m != null) Destroy(m);
    }

    private void Update()
    {
        if (graph == null) return;
        if (hologram && !holoBuilding && graph.IsLoaded) StartCoroutine(BuildHologram());
        if (!started) KeepGraphHidden();

        if (tourPending && tour != null && graph.IsLoaded && (intro == null || !intro.Playing))
        {
            tourPending = false;
            tour.StartTour();
        }

        if (homeAction != null && homeAction.WasPressedThisFrame() && started && !IsOpen) Open();
        if (IsOpen)
        {
            KeepOthersClosed();
            KeepInView(false);
        }
        UpdateHologram();
        UpdateWrist();
    }

    // ---- Start / home --------------------------------------------------------

    /// <summary>Shows the start panel in front of the user.</summary>
    public void Open()
    {
        ShowPage(false);
        UIKit.SetButtonLabel(startButton, started ? "Back to exploring" : "Start exploring");
        FindPanels();
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
        if (timeline != null && timeline.IsOpen) timeline.Close();
        canvas.gameObject.SetActive(true);
        KeepInView(true);
        openedAt = Time.time;
        UISounds.Play(UISounds.Open, canvas.transform.position);
    }

    /// <summary>
    /// One panel at a time: before the first start the dashboard / timeline stay closed (there is
    /// no graph yet); afterwards opening one of them closes the start panel.
    /// </summary>
    private void KeepOthersClosed()
    {
        FindPanels();
        bool other = (dashboard != null && dashboard.IsOpen) || (timeline != null && timeline.IsOpen);
        if (!other) return;
        if (started)
        {
            canvas.gameObject.SetActive(false);
            return;
        }
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
        if (timeline != null && timeline.IsOpen) timeline.Close();
    }

    private void FindPanels()
    {
        // Both are added at runtime (DataDashboard adds the TimelinePanel), so look again until found.
        if (dashboard == null) dashboard = FindFirstObjectByType<DataDashboard>();
        if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
    }

    private void Begin(bool withTour)
    {
        canvas.gameObject.SetActive(false);
        if (!started)
        {
            started = true;
            ShowGraph();
        }
        if (withTour)
        {
            if (tour == null) Debug.LogWarning("StartPanel: no GuidedTour in the scene; starting without the tour.");
            else if (tour.IsRunning) tour.StopTour();
            tourPending = tour != null;
        }
    }

    /// <summary>
    /// Until the user leaves the panel: no airports, routes or codes, and the intro waits
    /// (GraphIntro plays once it is enabled again and the graph has loaded).
    /// </summary>
    private void KeepGraphHidden()
    {
        if (intro == null) intro = FindFirstObjectByType<GraphIntro>();
        if (intro != null && intro.enabled) intro.enabled = false;
        if (!graph.IsLoaded) return;

        if (nodes == null) nodes = graph.transform.Find("Nodes");
        if (nodes != null && nodes.gameObject.activeSelf) nodes.gameObject.SetActive(false);
        if (!graphHidden)
        {
            graph.SetEdgeFade(0f);
            graphHidden = true;
        }
        // HubLabels builds its codes a little after the graph.
        if (labels == null) labels = GameObject.Find("HubLabels");
        if (labels != null && labels.activeSelf) labels.SetActive(false);
    }

    private void ShowGraph()
    {
        if (nodes != null) nodes.gameObject.SetActive(true);
        // The intro hides the codes again until it ends and fades the routes in.
        if (labels != null) labels.SetActive(true);
        if (intro != null && intro.playOnLoad) intro.enabled = true;
        else if (graphHidden) graph.SetEdgeFade(1f);
    }

    // ---- Placement -----------------------------------------------------------

    private void KeepInView(bool snap)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Vector3 forward = UIKit.FlatForward(head);
        Vector3 target = head.position + forward * distance + Vector3.up * heightOffset;

        Transform t = canvas.transform;
        Vector3 toPanel = Vector3.ProjectOnPlane(t.position - head.position, Vector3.up);
        bool away = Vector3.Angle(forward, toPanel) > followAngle || toPanel.magnitude > distance * 2f;
        if (snap) t.position = target;
        else if (away) t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
        else return;
        t.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(t.position - head.position, Vector3.up), Vector3.up);
    }

    // ---- Panel ---------------------------------------------------------------

    private void Build()
    {
        canvas = UIKit.WorldCanvas("Start Panel", Size);
        Transform root = canvas.transform;
        Image background = UIKit.Box(root, "Background", UIKit.PanelColor, UIKit.PanelRadius);
        UIKit.Stretch(background.rectTransform);

        homePage = UIKit.NewRect("Home", root);
        UIKit.Stretch(homePage);
        float w = Size.x - 2f * Pad;
        UIKit.Text(homePage, "FlightlyVR", UIKit.TitleSize * 1.6f, UIKit.TextColor, Pad, 70f, w, 80f,
                   TextAlignmentOptions.Center);
        UIKit.Text(homePage, "Explore Europe's air traffic network", UIKit.HeadingSize, UIKit.MutedTextColor,
                   Pad, 160f, w, 40f, TextAlignmentOptions.Center);

        float bx = Pad + 40f;
        float bw = w - 80f;
        startButton = UIKit.Button(homePage, "Start exploring", () => Begin(false), bx, 250f, bw, 92f,
                                   UIKit.ButtonStyle.Primary, UIKit.HeadingSize + 4f, UIIcon.Shape.Play);
        UIKit.Button(homePage, "Guided tour", () => Begin(true), bx, 366f, bw, 92f,
                     UIKit.ButtonStyle.Secondary, UIKit.HeadingSize + 4f, UIIcon.Shape.Globe);
        UIKit.Button(homePage, "Controls", () => ShowPage(true), bx, 482f, bw, 92f,
                     UIKit.ButtonStyle.Secondary, UIKit.HeadingSize + 4f, UIIcon.Shape.Grid);
        UIKit.Text(homePage, "Point and pull the trigger to choose", UIKit.SmallSize, UIKit.MutedTextColor,
                   Pad, 640f, w, 30f, TextAlignmentOptions.Center);

        controlsPage = UIKit.NewRect("Controls", root);
        UIKit.Stretch(controlsPage);
        BuildControls(controlsPage);
        controlsPage.gameObject.SetActive(false);
    }

    /// <summary>Controller column of ControlsHelp.Rows (the dashboard's Controls page has hands / editor / voice too).</summary>
    private void BuildControls(RectTransform page)
    {
        float w = Size.x - 2f * Pad;
        UIKit.Text(page, "Controls", UIKit.TitleSize, UIKit.TextColor, Pad, 36f, w, 50f);
        float y = 100f;
        const float rowHeight = 27f;
        foreach (ControlsHelp.Row row in ControlsHelp.Rows)
        {
            if (row.IsSection)
            {
                y += 6f;
                UIKit.Text(page, row.action, UIKit.SmallSize, UIKit.AccentColor, Pad, y, w, rowHeight);
            }
            else
            {
                UIKit.Text(page, row.action, UIKit.SmallSize, UIKit.TextColor, Pad, y, w * 0.48f, rowHeight);
                UIKit.Text(page, row.controller, UIKit.SmallSize, UIKit.MutedTextColor, Pad + w * 0.5f, y, w * 0.5f, rowHeight);
            }
            y += rowHeight;
        }
        UIKit.Text(page, "Voice: hold Y, speak, release. Say \"help\" for the list.", UIKit.SmallSize,
                   UIKit.MutedTextColor, Pad, y + 8f, w, rowHeight);
        UIKit.Button(page, "Back", () => ShowPage(false), Pad, Size.y - Pad - 56f, 200f, 56f,
                     UIKit.ButtonStyle.Secondary, UIKit.BodySize, UIIcon.Shape.Previous);
    }

    private void ShowPage(bool controls)
    {
        homePage.gameObject.SetActive(!controls);
        controlsPage.gameObject.SetActive(controls);
    }

    // ---- Hologram ------------------------------------------------------------

    /// <summary>
    /// Miniature of the loaded graph, built once: airports as small icosahedra in their colours, the
    /// busiest routes as lines. Two shapes: the 3D layout as it is at load (the shell) and, when the
    /// map file is there, the map of Europe (same projection as GeoMapView, on a small tilted disc
    /// with the map texture); the hologram morphs between them.
    /// </summary>
    private IEnumerator BuildHologram()
    {
        holoBuilding = true;
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        if (nodes.Count == 0) yield break;

        var routes = new List<GraphEdge>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (e.points != null && e.points.Length >= 2 && graph.Nodes.ContainsKey(e.sourceId) && graph.Nodes.ContainsKey(e.targetId)) routes.Add(e);
        }
        routes.Sort((a, b) => b.weight.CompareTo(a.weight));
        if (routes.Count > hologramRoutes) routes.RemoveRange(hologramRoutes, routes.Count - hologramRoutes);

        // Shell: the layout as it is now, fitted into hologramSize around its centre.
        var bounds = new Bounds(nodes[0].transform.position, Vector3.zero);
        foreach (GraphNode n in nodes) bounds.Encapsulate(n.transform.position);
        foreach (GraphEdge e in routes) foreach (Vector3 p in e.points) bounds.Encapsulate(p);
        Vector3 centre = bounds.center;
        float scale = hologramSize / Mathf.Max(0.01f, Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)));

        int count = nodes.Count;
        shellNode = new Vector3[count];
        nodeRadius = new float[count];
        nodeColor = new Color[count];
        var index = new Dictionary<GraphNode, int>();
        for (int i = 0; i < count; i++)
        {
            shellNode[i] = (nodes[i].transform.position - centre) * scale;
            nodeRadius[i] = Mathf.Max(0.003f, nodes[i].transform.lossyScale.x * scale * 0.6f);
            nodeColor[i] = nodes[i].baseColor;
            nodeColor[i].a = 1f;
            index[nodes[i]] = i;
        }
        // Routes: ArcSamples points each, from the end nearest to the arc's first point.
        shellLine = new Vector3[routes.Count * ArcSamples];
        routeEnds = new int[routes.Count * 2];
        var routeColor = new Color[routes.Count];
        for (int r = 0; r < routes.Count; r++)
        {
            GraphEdge e = routes[r];
            GraphNode a = graph.Nodes[e.sourceId];
            GraphNode b = graph.Nodes[e.targetId];
            if ((e.points[0] - a.transform.position).sqrMagnitude > (e.points[0] - b.transform.position).sqrMagnitude) (a, b) = (b, a);
            routeEnds[2 * r] = index[a];
            routeEnds[2 * r + 1] = index[b];
            for (int j = 0; j < ArcSamples; j++)
            {
                float f = (float)j / (ArcSamples - 1) * (e.points.Length - 1);
                int k = Mathf.Min((int)f, e.points.Length - 2);
                shellLine[r * ArcSamples + j] = (Vector3.Lerp(e.points[k], e.points[k + 1], f - k) - centre) * scale;
            }
            routeColor[r] = e.baseColor;
            routeColor[r].a = Mathf.Max(routeColor[r].a, 0.35f);
        }

        holo = new GameObject("Start Panel Hologram").transform;
        holoMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Panel Hologram (runtime)" };

        var lineColors = new Color[shellLine.Length];
        var lineIdx = new List<int>();
        for (int r = 0; r < routes.Count; r++)
        {
            for (int j = 0; j < ArcSamples; j++)
            {
                lineColors[r * ArcSamples + j] = routeColor[r];
                if (j > 0)
                {
                    lineIdx.Add(r * ArcSamples + j - 1);
                    lineIdx.Add(r * ArcSamples + j);
                }
            }
        }
        lineNow = (Vector3[])shellLine.Clone();
        routeMesh = AddHoloPart("Routes", lineNow, lineColors, lineIdx, MeshTopology.Lines, holoMaterial, holo);

        Icosahedron(out ico, out int[] faces);
        nodeNow = new Vector3[count * ico.Length];
        var nodeColors = new Color[nodeNow.Length];
        var tris = new List<int>();
        for (int i = 0; i < count; i++)
        {
            for (int v = 0; v < ico.Length; v++) nodeColors[i * ico.Length + v] = nodeColor[i];
            foreach (int f in faces) tris.Add(i * ico.Length + f);
        }
        airportMesh = AddHoloPart("Airports", nodeNow, nodeColors, tris, MeshTopology.Triangles, holoMaterial, holo);
        SetHoloShape(0f);
        holo.gameObject.SetActive(false);

        if (hologramMap) yield return BuildHologramMap(nodes);
    }

    /// <summary>Map shape: airports at their place on the map of Europe (those beyond it on the rim), arcs between them.</summary>
    private IEnumerator BuildHologramMap(List<GraphNode> nodes)
    {
        string json = null;
        yield return ReadStreamingText("geomap.json", text => json = text);
        if (json == null) yield break;
        double centerLat, centerLon;
        float radiusDeg, halfDeg;
        try
        {
            JObject map = JObject.Parse(json);
            centerLat = (double)map["centerLat"];
            centerLon = (double)map["centerLon"];
            radiusDeg = (float)map["radiusDeg"];
            halfDeg = (float)map["textureHalfSizeDeg"];
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"StartPanel: geomap.json could not be read ({e.Message}); the hologram stays 3D.");
            yield break;
        }
        if (radiusDeg <= 0f || halfDeg <= 0f) yield break;

        var projection = new GeoMapProjection { centerLat = centerLat, centerLon = centerLon };
        float k = hologramSize * 0.5f / (radiusDeg * 1.08f);          // degrees -> metres
        Quaternion tilt = Quaternion.Euler(-hologramMapTilt, 0f, 0f); // far (north) edge up, facing the viewer
        Vector3 OnMap(Vector2 deg, float lift) => tilt * new Vector3(deg.x * k, lift, deg.y * k);

        var planar = new Vector2[nodes.Count];
        mapNode = new Vector3[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            Vector2 p = projection.Project(nodes[i].lat, nodes[i].lon);
            if (p.magnitude > radiusDeg) p = p.normalized * radiusDeg * 1.05f; // beyond the map: on the rim
            planar[i] = p;
            mapNode[i] = OnMap(p, 0.006f);
        }
        mapLine = new Vector3[shellLine.Length];
        int routes = shellLine.Length / ArcSamples;
        for (int r = 0; r < routes; r++)
        {
            Vector2 a = planar[routeEnds[2 * r]];
            Vector2 b = planar[routeEnds[2 * r + 1]];
            float height = Vector2.Distance(a, b) * k * 0.3f;
            for (int j = 0; j < ArcSamples; j++)
            {
                float t = (float)j / (ArcSamples - 1);
                mapLine[r * ArcSamples + j] = OnMap(Vector2.Lerp(a, b, t), 0.004f + height * Mathf.Sin(Mathf.PI * t));
            }
        }

        // Disc with the map texture (taken from GeoMapView's map when it has loaded it, so it is not loaded twice).
        const int segments = 64;
        var discVerts = new Vector3[segments + 1];
        var discUV = new Vector2[segments + 1];
        var discColors = new Color[segments + 1];
        var discTris = new List<int>();
        discVerts[0] = OnMap(Vector2.zero, 0f);
        discUV[0] = new Vector2(0.5f, 0.5f);
        for (int s = 0; s < segments; s++)
        {
            float angle = 2f * Mathf.PI * s / segments;
            Vector2 p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radiusDeg;
            discVerts[s + 1] = OnMap(p, 0f);
            discUV[s + 1] = new Vector2(0.5f + p.x / (2f * halfDeg), 0.5f + p.y / (2f * halfDeg));
            discTris.Add(0);
            discTris.Add(1 + (s + 1) % segments);
            discTris.Add(1 + s);
        }
        for (int i = 0; i < discColors.Length; i++) discColors[i] = Color.white;
        holoDiscMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Panel Hologram Map (runtime)" };
        float wait = Time.time + 15f;
        Texture texture = MapTexture();
        while (texture == null && Time.time < wait)
        {
            yield return new WaitForSeconds(0.5f);
            texture = MapTexture();
        }
        if (texture != null) holoDiscMaterial.mainTexture = texture;
        else holoDiscMaterial.mainTexture = Texture2D.whiteTexture;
        if (texture == null) holoDiscMaterial.color = UIKit.SurfaceColor;
        Mesh disc = AddHoloPart("Map", discVerts, discColors, discTris, MeshTopology.Triangles, holoDiscMaterial, holo);
        disc.SetUVs(0, discUV);
        holoDisc = holo.Find("Map");
        holoDisc.gameObject.SetActive(false);
        hasMapShape = true;
    }

    /// <summary>GeoMapView's map texture, once it has built its map (null before / without it).</summary>
    private static Texture MapTexture()
    {
        foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Material m = r.sharedMaterial;
            if (m == null) continue;
            // Read only slots the shader has (Material.mainTexture logs an error on shaders without one).
            Texture t = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (t != null && t.name == "Geo Map Texture") return t;
        }
        return null;
    }

    private static IEnumerator ReadStreamingText(string fileName, System.Action<string> onText)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        string uri = path.Contains("://") ? path : "file://" + path;
        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();
            onText(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : null);
        }
    }

    private Mesh AddHoloPart(string name, Vector3[] verts, Color[] colors, List<int> indices, MeshTopology topology,
                             Material material, Transform parent)
    {
        var mesh = new Mesh { name = "Start Panel Hologram " + name };
        if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.vertices = verts;
        mesh.colors = colors;
        mesh.SetIndices(indices, topology, 0);
        holoMeshes.Add(mesh);
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return mesh;
    }

    /// <summary>0 = 3D shell, 1 = map; airports and routes in between are interpolated.</summary>
    private void SetHoloShape(float m)
    {
        if (Mathf.Abs(m - shownShape) < 1e-4f) return;
        shownShape = m;
        bool map = m > 0f && mapNode != null;
        for (int i = 0; i < lineNow.Length; i++) lineNow[i] = map ? Vector3.LerpUnclamped(shellLine[i], mapLine[i], m) : shellLine[i];
        int n = ico.Length;
        for (int i = 0; i < shellNode.Length; i++)
        {
            Vector3 at = map ? Vector3.LerpUnclamped(shellNode[i], mapNode[i], m) : shellNode[i];
            for (int v = 0; v < n; v++) nodeNow[i * n + v] = at + ico[v] * nodeRadius[i];
        }
        routeMesh.vertices = lineNow;
        routeMesh.RecalculateBounds();
        airportMesh.vertices = nodeNow;
        airportMesh.RecalculateBounds();
    }

    /// <summary>
    /// Above the panel, a little towards the viewer. Shell: turns slowly; then it morphs onto the map
    /// (turned to face you), stays a while, and morphs back. Fades in when the panel opens.
    /// </summary>
    private void UpdateHologram()
    {
        if (holo == null) return;
        bool show = IsOpen && hologram;
        if (holo.gameObject.activeSelf != show) holo.gameObject.SetActive(show);
        if (!show) return;

        // Shape over time: hold 3D, morph to the map, hold, morph back.
        float m = 0f;
        if (hasMapShape)
        {
            float hold = Mathf.Max(0.1f, hologramHoldSeconds);
            float morph = Mathf.Max(0.1f, hologramMorphSeconds);
            float t = (Time.time - openedAt) % (2f * (hold + morph));
            if (t < hold) m = 0f;
            else if (t < hold + morph) m = (t - hold) / morph;
            else if (t < 2f * hold + morph) m = 1f;
            else m = 1f - (t - 2f * hold - morph) / morph;
            m = m * m * (3f - 2f * m);
        }
        SetHoloShape(m);
        if (holoDisc != null)
        {
            if (holoDisc.gameObject.activeSelf != m > 0.01f) holoDisc.gameObject.SetActive(m > 0.01f);
            Color c = holoDiscMaterial.color;
            c.a = Mathf.Clamp01(m * 1.5f - 0.3f) * 0.9f;
            holoDiscMaterial.color = c;
        }

        Transform panel = canvas.transform;
        float panelHalf = Size.y * 0.5f * panel.lossyScale.y;
        float bob = Mathf.Sin(Time.time * 0.8f) * 0.01f;
        holo.position = panel.position + Vector3.up * (panelHalf + 0.05f + hologramSize * 0.5f + bob) - panel.forward * 0.1f;
        holoSpin += hologramSpin * Time.deltaTime * (1f - m); // only the 3D shape turns
        Quaternion facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(panel.forward, Vector3.up), Vector3.up);
        holo.rotation = facing * Quaternion.Slerp(Quaternion.Euler(0f, holoSpin, 0f), Quaternion.identity, m);
        float fade = Mathf.Clamp01((Time.time - openedAt) / 0.6f);
        holo.localScale = Vector3.one * Mathf.Lerp(0.85f, 1f, fade);
        holoMaterial.color = new Color(1f, 1f, 1f, fade);
    }

    private static void Icosahedron(out Vector3[] vertices, out int[] faces)
    {
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        vertices = new[]
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i].normalized;
        faces = new[]
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
    }

    // ---- Wrist Home button ---------------------------------------------------

    private void BuildWrist()
    {
        const float size = 120f; // canvas units; scaled to wristButtonSize below
        wrist = UIKit.WorldCanvas("Start Panel Wrist Home", new Vector2(size, size));
        wrist.transform.localScale = Vector3.one * (wristButtonSize / size);
        UIKit.Button(wrist.transform, "Home", Open, 0f, 0f, size, size, UIKit.ButtonStyle.Primary, 26f);
        wrist.gameObject.SetActive(false);
    }

    private void UpdateWrist()
    {
        // Hidden while the tour runs (it locks input and has its own caption).
        bool show = wristButton && started && !IsOpen && (tour == null || !tour.IsRunning);
        Camera cam = Camera.main;
        if (show && cam != null && FindLeftHand())
        {
            Transform hand = leftHand.transform;
            Vector3 position = hand.TransformPoint(wristOffset);
            Vector3 toEyes = (cam.transform.position - position).normalized;
            show = Vector3.Dot(hand.TransformDirection(palmAxis).normalized, toEyes) >= showThreshold;
            if (show)
            {
                wrist.transform.position = position;
                wrist.transform.rotation = Quaternion.LookRotation(-toEyes, Vector3.up);
            }
        }
        else show = false;
        if (wrist.gameObject.activeSelf != show) wrist.gameObject.SetActive(show);
    }

    private bool FindLeftHand()
    {
        if (leftHand != null && leftHand.isActiveAndEnabled) return true;
        leftHand = null;
        if (Time.unscaledTime < nextHandSearch) return false;
        nextHandSearch = Time.unscaledTime + 1f;
        foreach (NearFarInteractor interactor in FindObjectsByType<NearFarInteractor>(FindObjectsSortMode.None))
        {
            if (interactor.isActiveAndEnabled && interactor.handedness == InteractorHandedness.Left)
            {
                leftHand = interactor;
                break;
            }
        }
        return leftHand != null;
    }
}
