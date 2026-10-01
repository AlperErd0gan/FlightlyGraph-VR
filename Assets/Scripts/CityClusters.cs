using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Groups the airports of one city (TASKS X5): with grouping on, every city with at
/// least two airports in the graph collapses into one city node. Its airports fly to
/// the busiest one's spot and hide; the city node (sized by their summed traffic, with
/// a ring and a "London · 6" label) stands for them and their routes start there, so
/// they read as the city's routes. Selecting the city node expands it: the airports
/// fly back to their own places and the routes follow; selecting the city's label
/// groups it again. Selecting a hidden airport (dashboard lists, routes) expands its
/// city first. Works with the timeline and the filters (sizes follow the view).
/// Cities are metropolitan areas listed by ICAO code (metros, editable in the
/// Inspector; defaults follow the IATA multi-airport city codes), not the data's
/// `city` text, which names municipalities (CDG: "Roissy-en-France", MXP: "Ferno").
/// Toggle: the dashboard's Clusters page, C in the Editor, or SetGrouped().
/// </summary>
public class CityClusters : MonoBehaviour
{
    [System.Serializable]
    public class Metro
    {
        public string name;
        [Tooltip("ICAO codes of the city's airports.")]
        public string[] airports;
    }

    public GraphLoader graph;
    public GraphSelector selector;
    public bool startGrouped = false;
    [Tooltip("Seconds for the airports to fly in or out.")]
    public float animationSeconds = 0.45f;
    [Tooltip("Collider radius relative to the city sphere (like XRGraphInput.colliderScale).")]
    public float colliderScale = 1.5f;
    public Color ringColor = new Color(1f, 1f, 1f, 0.75f);
    public float ringWidth = 0.004f;
    [Tooltip("TextMeshPro 3D font size of the city label; 10 = 1 m line height.")]
    public float labelFontSize = 0.7f;
    public Color labelColor = new Color(1f, 0.85f, 0.5f, 1f);
    [Tooltip("Optional material for the ring (vertex colours, e.g. Sprites/Default). If unset, Sprites/Default is created.")]
    public Material ringMaterial;
    public Metro[] metros = DefaultMetros();

    private class City
    {
        public string name;
        public List<GraphNode> members;   // busiest first; members[0] is the anchor
        public Vector3[] homes;           // members' own local positions in the graph
        public List<GraphEdge> edges;     // routes touching a member
        public List<GraphEdge> inner;     // routes between two members
        public float t;                   // 0 = at home (expanded) .. 1 = gathered (collapsed)
        public float target;
        public GameObject sphere;         // the city node (collapsed)
        public Renderer sphereRenderer;
        public LineRenderer ring;
        public TextMeshPro label;
        public BoxCollider labelCollider;
        public bool Collapsed => target >= 1f;
        public GraphNode Anchor => members[0];
    }

    private const int RingSegments = 48;
    private readonly List<City> cities = new List<City>();
    private readonly Dictionary<GraphNode, City> cityOf = new Dictionary<GraphNode, City>();
    private readonly Dictionary<Collider, City> cityByCollider = new Dictionary<Collider, City>();
    private Transform root;
    private Material runtimeRingMaterial;
    private InputAction toggleKey;
    private bool built;

    /// <summary>True while grouping is on (cities collapse by default).</summary>
    public bool Grouped { get; private set; }
    public int CityCount => cities.Count;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        toggleKey = new InputAction("Toggle City Groups", InputActionType.Button, "<Keyboard>/c");
    }

    private void OnEnable()
    {
        toggleKey.Enable();
        if (selector != null)
        {
            selector.NodeSelected += OnNodeSelected;
            selector.PathSelected += OnPathSelected;
            selector.ColliderClicked += OnColliderClicked;
        }
    }

    private void OnDisable()
    {
        toggleKey.Disable();
        if (selector != null)
        {
            selector.NodeSelected -= OnNodeSelected;
            selector.PathSelected -= OnPathSelected;
            selector.ColliderClicked -= OnColliderClicked;
        }
    }

    private void OnDestroy()
    {
        toggleKey.Dispose();
        if (root != null) Destroy(root.gameObject);
        if (runtimeRingMaterial != null) Destroy(runtimeRingMaterial);
    }

    private void Update()
    {
        if (graph == null || !graph.IsLoaded) return;
        if (!built)
        {
            Build();
            if (startGrouped) SetGrouped(true, true);
        }
        if (GuidedTour.InputLocked)
        {
            // The guided tour talks about single airports: show them all.
            if (Grouped) SetGrouped(false);
        }
        else if (toggleKey.WasPressedThisFrame())
        {
            SetGrouped(!Grouped);
        }
        Animate();
    }

    private void LateUpdate()
    {
        if (!built) return;
        Camera cam = Camera.main;
        foreach (City city in cities) UpdateVisuals(city, cam);
    }

    // ---- Public controls -------------------------------------------------

    /// <summary>Grouping on: every city collapses; off: every city expands (animated unless instant).</summary>
    public void SetGrouped(bool grouped, bool instant = false)
    {
        Grouped = grouped;
        foreach (City city in cities) SetCollapsed(city, grouped, instant);
    }

    /// <summary>"London (6), Paris (3), ..." for the dashboard.</summary>
    public string Summary(int maxCities = 8)
    {
        var parts = new List<string>();
        for (int i = 0; i < cities.Count && i < maxCities; i++) parts.Add(cities[i].name + " (" + cities[i].members.Count + ")");
        if (cities.Count > maxCities) parts.Add("...");
        return string.Join(", ", parts);
    }

    /// <summary>True for an airport hidden inside a collapsed (or collapsing) city.</summary>
    public bool IsHidden(GraphNode node)
    {
        return cityOf.TryGetValue(node, out City city) && city.t > 0f;
    }

    /// <summary>Collapses or expands the city of `node` (no-op if it belongs to none).</summary>
    public void SetCityCollapsed(GraphNode node, bool collapsed)
    {
        if (cityOf.TryGetValue(node, out City city)) SetCollapsed(city, collapsed, false);
    }

    // ---- Setup -----------------------------------------------------------

    private void Build()
    {
        built = true;
        root = new GameObject("City Groups").transform;
        root.SetParent(graph.transform, false);

        var used = new HashSet<GraphNode>();
        foreach (Metro metro in metros)
        {
            if (metro == null || metro.airports == null) continue;
            var members = new List<GraphNode>();
            foreach (string icao in metro.airports)
            {
                if (!string.IsNullOrEmpty(icao) && graph.Nodes.TryGetValue(icao, out GraphNode node) && used.Add(node)) members.Add(node);
            }
            if (members.Count < 2) continue;
            members.Sort((a, b) => a.value != b.value ? b.value.CompareTo(a.value) : string.CompareOrdinal(a.id, b.id));

            var city = new City
            {
                name = string.IsNullOrEmpty(metro.name) ? members[0].ShortCode : metro.name,
                members = members,
                homes = new Vector3[members.Count],
                edges = new List<GraphEdge>(),
                inner = new List<GraphEdge>(),
            };
            var memberSet = new HashSet<GraphNode>(members);
            var edgeSet = new HashSet<GraphEdge>();
            for (int i = 0; i < members.Count; i++)
            {
                city.homes[i] = members[i].transform.localPosition;
                cityOf[members[i]] = city;
                foreach (GraphEdge e in graph.EdgesOf(members[i].id))
                {
                    if (!edgeSet.Add(e)) continue;
                    city.edges.Add(e);
                    if (memberSet.Contains(graph.Nodes[e.sourceId]) && memberSet.Contains(graph.Nodes[e.targetId])) city.inner.Add(e);
                }
            }
            BuildCityObjects(city);
            cities.Add(city);
        }
        // Biggest cities first (dashboard summary).
        cities.Sort((a, b) => TotalValue(b).CompareTo(TotalValue(a)));
        Debug.Log($"CityClusters: {cities.Count} cities with several airports in the graph: {Summary(20)}");
    }

    private static long TotalValue(City city)
    {
        long sum = 0;
        foreach (GraphNode m in city.members) sum += m.value;
        return sum;
    }

    private void BuildCityObjects(City city)
    {
        // City node: a sphere like the airports, with a collider for XR rays and the mouse.
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "City " + city.name;
        sphere.transform.SetParent(root, false);
        SphereCollider sphereCollider = sphere.GetComponent<SphereCollider>();
        sphereCollider.radius = 0.5f * colliderScale;
        city.sphere = sphere;
        city.sphereRenderer = sphere.GetComponent<Renderer>();
        AddInteractable(sphere, city);
        cityByCollider[sphereCollider] = city;
        sphere.SetActive(false);

        var ringObject = new GameObject("Ring " + city.name);
        ringObject.transform.SetParent(root, false);
        LineRenderer ring = ringObject.AddComponent<LineRenderer>();
        ring.useWorldSpace = true;
        ring.loop = true;
        ring.positionCount = RingSegments;
        ring.startWidth = ring.endWidth = ringWidth;
        ring.startColor = ring.endColor = ringColor;
        ring.sharedMaterial = RingMaterial();
        ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ring.receiveShadows = false;
        ring.enabled = false;
        city.ring = ring;

        var labelObject = new GameObject("Label " + city.name);
        labelObject.transform.SetParent(root, false);
        TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
        label.fontSize = labelFontSize;
        label.color = labelColor;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Bottom;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.rectTransform.pivot = new Vector2(0.5f, 0f);
        label.rectTransform.sizeDelta = new Vector2(1f, 0f);
        // XRBaseInteractable collects colliders in Awake, so the collider must exist first.
        city.labelCollider = labelObject.AddComponent<BoxCollider>();
        AddInteractable(labelObject, city);
        cityByCollider[city.labelCollider] = city;
        city.label = label;
        labelObject.SetActive(false);
    }

    private void AddInteractable(GameObject target, City city)
    {
        XRSimpleInteractable interactable = target.AddComponent<XRSimpleInteractable>();
        interactable.selectEntered.AddListener(_ =>
        {
            if (!GuidedTour.InputLocked) SetCollapsed(city, !city.Collapsed, false);
        });
    }

    private Material RingMaterial()
    {
        if (ringMaterial != null) return ringMaterial;
        if (runtimeRingMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) runtimeRingMaterial = new Material(shader) { name = "City Ring (runtime)" };
        }
        return runtimeRingMaterial;
    }

    // ---- Collapse / expand -------------------------------------------------

    private void SetCollapsed(City city, bool collapsed, bool instant)
    {
        city.target = collapsed ? 1f : 0f;
        if (collapsed && city.inner.Count > 0)
        {
            // Routes between the city's own airports would shrink to a point: hide them.
            graph.SetEdgesSuppressed(city.inner, true);
            // That refilters the edges, which drops the selection's revealed routes: draw it again.
            if (selector != null) selector.RefreshSelection();
        }
        else
        {
            foreach (GraphNode m in city.members) m.gameObject.SetActive(true);
        }
        if (instant)
        {
            city.t = city.target;
            ApplyPositions(city);
            FinishIfSettled(city);
        }
    }

    private void Animate()
    {
        float step = animationSeconds > 0f ? Time.deltaTime / animationSeconds : 1f;
        var moving = new List<GraphEdge>();
        foreach (City city in cities)
        {
            if (Mathf.Approximately(city.t, city.target)) continue;
            city.t = Mathf.MoveTowards(city.t, city.target, step);
            ApplyPositions(city);
            moving.AddRange(city.edges);
            FinishIfSettled(city);
        }
        if (moving.Count > 0) graph.RebuildEdges(moving);
    }

    /// <summary>Members between their homes (t = 0) and the anchor's home (t = 1), eased.</summary>
    private void ApplyPositions(City city)
    {
        float s = city.t * city.t * (3f - 2f * city.t);
        Vector3 anchorHome = city.homes[0];
        for (int i = 0; i < city.members.Count; i++)
        {
            city.members[i].transform.localPosition = Vector3.Lerp(city.homes[i], anchorHome, s);
        }
    }

    private void FinishIfSettled(City city)
    {
        if (!Mathf.Approximately(city.t, city.target)) return;
        if (city.target >= 1f)
        {
            foreach (GraphNode m in city.members) m.gameObject.SetActive(false);
        }
        else if (city.inner.Count > 0)
        {
            graph.SetEdgesSuppressed(city.inner, false);
            if (selector != null) selector.RefreshSelection();
        }
        graph.RebuildEdges(city.edges);
    }

    // ---- Visuals -------------------------------------------------------

    private void UpdateVisuals(City city, Camera cam)
    {
        bool collapsed = city.t >= 1f;
        bool expanded = city.t <= 0f;
        if (city.sphere.activeSelf != collapsed) city.sphere.SetActive(collapsed);
        city.ring.enabled = collapsed;
        // A collapsed city always shows its name; an expanded one only while grouping is on (to group it again).
        bool showLabel = collapsed || (Grouped && expanded);
        if (city.label.gameObject.activeSelf != showLabel) city.label.gameObject.SetActive(showLabel);
        if (!collapsed && !showLabel) return;

        Vector3 centre = graph.transform.TransformPoint(city.homes[0]);
        float size;
        if (collapsed)
        {
            // Size from the airports' summed traffic in the current view (month, filter).
            float shown = 0f;
            foreach (GraphNode m in city.members) shown += graph.ShownValue(m);
            size = graph.NodeSize(shown);
            city.sphere.transform.position = centre;
            city.sphere.transform.localScale = Vector3.one * size;
            // Colour of the brightest airport (keeps selection highlight / dimming and view-mode colours).
            Material brightest = BrightestMaterial(city);
            if (brightest != null) city.sphereRenderer.sharedMaterial = brightest;
        }
        else
        {
            size = city.Anchor.transform.lossyScale.y;
        }

        if (cam == null) return;
        Transform head = cam.transform;
        if (collapsed)
        {
            float radius = size * 0.5f + 0.02f;
            for (int i = 0; i < RingSegments; i++)
            {
                float a = 2f * Mathf.PI * i / RingSegments;
                city.ring.SetPosition(i, centre + (head.right * Mathf.Cos(a) + head.up * Mathf.Sin(a)) * radius);
            }
        }
        if (showLabel)
        {
            string text = collapsed ? city.name + " · " + city.members.Count : city.name + " · group";
            if (city.label.text != text)
            {
                city.label.text = text;
                city.label.ForceMeshUpdate();
                Bounds bounds = city.label.textBounds;
                city.labelCollider.center = bounds.center;
                city.labelCollider.size = new Vector3(bounds.size.x + 0.04f, bounds.size.y + 0.03f, 0.02f);
            }
            // Expanded: above the anchor airport's own hub label.
            float lift = size * 0.5f + (collapsed ? 0.03f : 0.14f);
            Transform label = city.label.transform;
            label.position = centre + Vector3.up * lift;
            // TMP text reads correctly when its +Z points away from the viewer.
            Vector3 away = label.position - head.position;
            if (away.sqrMagnitude > 1e-6f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }

    private static Material BrightestMaterial(City city)
    {
        Material best = null;
        float bestBrightness = -1f;
        foreach (GraphNode m in city.members)
        {
            Renderer r = m.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) continue;
            Color c = r.sharedMaterial.color;
            float brightness = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (brightness > bestBrightness)
            {
                bestBrightness = brightness;
                best = r.sharedMaterial;
            }
        }
        return best;
    }

    // ---- Selection -----------------------------------------------------

    /// <summary>A hidden airport was selected (dashboard list, route): show its city's airports.</summary>
    private void OnNodeSelected(GraphNode node)
    {
        if (cityOf.TryGetValue(node, out City city) && city.target > 0f) SetCollapsed(city, false, false);
    }

    private void OnPathSelected(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        foreach (GraphNode node in nodes) OnNodeSelected(node);
    }

    /// <summary>Desktop click on a city node or label (GraphSelector.ColliderClicked).</summary>
    private bool OnColliderClicked(Collider collider)
    {
        if (!cityByCollider.TryGetValue(collider, out City city)) return false;
        SetCollapsed(city, !city.Collapsed, false);
        return true;
    }

    // ---- Default cities ------------------------------------------------

    /// <summary>Multi-airport cities (IATA metropolitan areas), by ICAO code; only airports in the graph count.</summary>
    private static Metro[] DefaultMetros()
    {
        return new[]
        {
            M("London", "EGLL", "EGKK", "EGSS", "EGGW", "EGLC", "EGMC", "EGKB"),
            M("Paris", "LFPG", "LFPO", "LFPB"),
            M("Istanbul", "LTFM", "LTFJ", "LTBA"),
            M("Moscow", "UUEE", "UUDD", "UUWW", "UUBW"),
            M("Rome", "LIRF", "LIRA"),
            M("Milan", "LIMC", "LIML", "LIME"),
            M("Stockholm", "ESSA", "ESSB", "ESKN", "ESOW"),
            M("Oslo", "ENGM", "ENTO", "ENRY"),
            M("Berlin", "EDDB", "EDDT"),
            M("Belfast", "EGAA", "EGAC"),
            M("Bucharest", "LROP", "LRBS"),
            M("Kyiv", "UKBB", "UKKK"),
            M("Tenerife", "GCTS", "GCXO"),
            M("Reykjavik", "BIKF", "BIRK"),
            M("New York", "KJFK", "KEWR", "KLGA"),
            M("Washington", "KIAD", "KDCA", "KBWI"),
            M("Chicago", "KORD", "KMDW"),
            M("Toronto", "CYYZ", "CYTZ"),
            M("Montreal", "CYUL", "CYMX"),
            M("Sao Paulo", "SBGR", "SBSP", "SBKP"),
            M("Buenos Aires", "SAEZ", "SABE"),
            M("Dubai", "OMDB", "OMDW"),
            M("Tokyo", "RJTT", "RJAA"),
            M("Seoul", "RKSI", "RKSS"),
            M("Beijing", "ZBAA", "ZBAD"),
            M("Shanghai", "ZSPD", "ZSSS"),
            M("Bangkok", "VTBS", "VTBD"),
        };
    }

    private static Metro M(string name, params string[] airports)
    {
        return new Metro { name = name, airports = airports };
    }
}
