using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

/// <summary>
/// Geographic view: the airports leave the 3D network layout and land on a large, gently
/// curved map of Europe in front of the graph centre (build_geomap.py's projection and
/// texture, bent by GeoMapProjection). Routes arc over the map. Airports beyond Europe
/// (New York, Dubai, Singapore, ...) sit on a band outside the map's rim, in their true
/// direction from the map centre, so it is clear they are off the map.
/// Switch: B (right controller; toggleBinding), M in the Editor, the dashboard or voice.
/// Selection, info card, timeline, filters, view modes and city groups work the same on
/// the map. The guided tour runs in the 3D layout (starting it switches back).
/// The map never goes into the floor: when its lowest part (the band and the names
/// beyond it) would come closer than floorClearance to GraphLoader.FloorY, the whole
/// map is raised, also later when the floor or the graph moves.
/// Dome (SetDome, DomeToggle's button): you at the centre of the Earth. The same projection
/// on the inside of a sphere of domeRadius around the graph centre (you, after Go to graph
/// centre), at true scale: every airport and route in its real direction from the Earth's
/// centre. The sharp map of Europe is in front of you, domeElevation up (north up), the
/// rest of the world (globe_dark.png from build_globe.py, equirectangular) on a sphere just
/// behind it; airports beyond Europe sit at their true places with their names. Everything on it is scaled by ViewScale. Leaving the map always
/// returns to the flat map.
/// </summary>
public class GeoMapView : MonoBehaviour, IGraphLayout
{
    public GraphLoader graph;
    [Tooltip("Airports collapsed into a city land on the city's spot. Found in the scene if unset.")]
    public CityClusters cities;

    [Header("Files (StreamingAssets)")]
    public string mapFileName = "geomap.json";
    [Tooltip("Texture used instead of the one named in the map file (same projection as build_geomap.py). Empty = the map file's.")]
    public string textureOverride = "";

    [Header("Placement (from the graph centre: your eyes after Go to graph centre)")]
    [Tooltip("Distance of the map centre in front of the graph centre (m).")]
    public float distance = 1.8f;
    [Tooltip("Height of the map centre relative to the graph centre (m).")]
    public float height = -0.35f;
    [Tooltip("How far the map leans back (deg): 0 = upright like a wall, 90 = flat like a table.")]
    [Range(0f, 90f)] public float tilt = 20f;
    [Tooltip("Radius of the map of Europe on the surface (m).")]
    public float mapRadius = 1.3f;
    [Tooltip("Curvature (m): > 0 bulges towards you like part of a globe, < 0 curves around you, 0 = flat.")]
    public float curvatureRadius = 3.5f;
    [Tooltip("Lowest part of the map (band and the names beyond it) stays this far above the floor (m); the map is raised when it would reach lower.")]
    public float floorClearance = 0.1f;

    [Header("Dome: you at the centre of the Earth (DomeToggle)")]
    [Tooltip("Distance from the graph centre (your eyes) to the globe's surface (m).")]
    public float domeRadius = 6f;
    [Tooltip("Height (deg) of the map of Europe's centre above the horizon, in front of you: 90 = overhead (neck strain), " +
             "lower = more of the southern airports under the floor (40: only Sao Paulo).")]
    [Range(0f, 90f)] public float domeElevation = 40f;
    [Tooltip("World texture for the dome (StreamingAssets, equirectangular; scripts/build_globe.py). Loaded the first time the dome opens.")]
    public string globeTextureFile = "globe_dark.png";
    [Tooltip("Airports and routes this far (m, times ViewScale) off the map's surface, towards you: small, so each airport touches its spot " +
             "(the world texture lies 0.05 m behind the surface). Was 0.12: airports floated off the map (B31).")]
    public float domeNodeLift = 0.01f;
    [Tooltip("Airport codes (HubLabels) on the flat map and the dome. Off: codes only in the graph view; names come from the map and HoverLabel (B30).")]
    public bool showAirportCodes = false;
    [Tooltip("Extra latitude / longitude grid over the globe (deg); 0 = none (the world texture has a 10 deg grid).")]
    public float graticuleStep = 0f;
    public Color graticuleColor = new Color(0.12f, 0.2f, 0.32f, 1f);
    public Color graticuleMainColor = new Color(0.22f, 0.38f, 0.58f, 1f);
    public Color globeInsideColor = new Color(0.02f, 0.03f, 0.06f, 1f);

    [Header("Airports and routes on the map")]
    [Tooltip("Airport size multiplier on the map (smaller than in the 3D layout, the map is closer). Upper bound: see maxNodeDiameter.")]
    public float nodeScale = 0.6f;
    [Tooltip("Diameter (m) of the biggest airport on the map; all airports shrink by the same factor when it would be bigger, " +
             "whatever the 3D sizes of this scene. 0 = only nodeScale. Live in Play mode.")]
    public float maxNodeDiameter = 0.04f;
    [Tooltip("Route width multiplier on the map (highlighted routes too: an airport's ~170 routes must not cover it).")]
    public float edgeWidthScale = 0.35f;
    [Tooltip("Route arc height as a fraction of its length on the map.")]
    public float arcHeight = 0.12f;
    [Tooltip("Highest arc above the map (m), so the long intercontinental routes do not tower towards you.")]
    public float maxArcLift = 0.2f;
    [Tooltip("Airport centres above the map surface (m).")]
    public float nodeLift = 0.012f;

    [Header("Outside the map")]
    [Tooltip("Gap between the map's rim and the band of airports beyond Europe (m).")]
    public float bandGap = 0.02f;
    [Tooltip("Width of that band (m).")]
    public float bandWidth = 0.17f;
    [Tooltip("Smallest distance between two airports on the band (m).")]
    public float outsideSpacing = 0.09f;

    [Header("Look")]
    [Tooltip("Optional material for the map (an Unlit one with a base map). If unset, URP/Unlit is created; assign one for builds so the shader is included.")]
    public Material mapMaterial;
    public bool showCountryNames = true;
    public Color countryNameColor = new Color(0.66f, 0.76f, 0.9f, 0.55f);
    public Color rimColor = new Color(0.30f, 0.75f, 1f, 1f);
    public Color bandColor = new Color(0.075f, 0.09f, 0.13f, 1f);
    public Color outsideNameColor = new Color(1f, 0.82f, 0.55f, 0.95f);
    [Tooltip("Size of the country names (1 = the original size).")]
    public float countryNameScale = 0.8f;
    [Tooltip("Font size of the names beyond the map (\"DXB · Dubai\").")]
    public float outsideNameSize = 0.22f;
    [Tooltip("Beyond the map, only the busiest airport of each city gets a name.")]
    public bool onePerCity = true;
    [Tooltip("Leave out a name that would overlap one already placed (countries by importance, then airports by traffic).")]
    public bool hideOverlappingNames = true;
    [Tooltip("Space kept around each name (m).")]
    public float nameGap = 0.004f;

    [Header("Switch")]
    [Tooltip("Controller button that switches 3D layout <-> map (Input System path). Default: B. Empty = no controller button.")]
    public string toggleBinding = "<XRController>{RightHand}/{SecondaryButton}";
    public float transitionSeconds = 1.2f;
    public bool startOnMap = false;

    /// <summary>True while (or after switching) to the map.</summary>
    public bool IsMap { get; private set; }
    /// <summary>The map file is loaded and the graph is ready.</summary>
    public bool Ready { get; private set; }
    public bool Switching => transition != null;
    /// <summary>True while the map is bent around the viewer (dome) instead of flat in front.</summary>
    public bool IsDome { get; private set; }
    /// <summary>
    /// Size of everything on the map (airports, routes, names, band, codes, aircraft) relative to the
    /// flat map: the dome is domeRadius away instead of `distance`, so it is scaled to look the same.
    /// </summary>
    public float ViewScale => IsDome ? domeRadius / Mathf.Max(0.5f, distance) : 1f;
    /// <summary>Raised when a switch has finished (true = map).</summary>
    public event System.Action<bool> ModeChanged;
    /// <summary>Raised after the map was placed again (raised above a moved floor): positions on it changed.</summary>
    public event System.Action Moved;
    public GeoMapProjection Projection => projection;
    /// <summary>Radius of the map of Europe in degrees of arc (beyond it: the outer band).</summary>
    public float MapRadiusDegrees => map != null ? (IsDome ? 180f : map.radiusDeg) : 0f; // the dome has no "beyond the map"
    public int OutsideCount => outside.Count;
    public bool IsOutside(GraphNode node) => outside.Contains(node);

    // IGraphLayout
    public float NodeScale
    {
        get
        {
            if (maxNodeDiameter <= 0f || graph == null || !graph.IsLoaded) return nodeScale * ViewScale;
            if (scaleFrame != Time.frameCount)
            {
                // Size of the biggest airport without a layout (nodeBaseSize + nodeSizePerSqrtValue * sqrt(value)).
                float biggest = 0f;
                foreach (GraphNode node in graph.Nodes.Values)
                {
                    biggest = Mathf.Max(biggest, graph.nodeBaseSize + graph.nodeSizePerSqrtValue * Mathf.Sqrt(Mathf.Max(0, node.value)));
                }
                cachedScale = (biggest > 1e-6f ? Mathf.Min(nodeScale, maxNodeDiameter / biggest) : nodeScale) * ViewScale;
                scaleFrame = Time.frameCount;
            }
            return cachedScale;
        }
    }

    private int scaleFrame = -1;
    private float cachedScale = 1f;

    private void OnValidate()
    {
        // Size changes in the Inspector apply at once while the map is shown.
        scaleFrame = -1;
        if (Application.isPlaying && IsMap && graph != null && graph.IsLoaded && ReferenceEquals(graph.Layout, this)) graph.SetLayout(this);
    }
    public float EdgeWidthScale => edgeWidthScale * ViewScale;

    private class MapFile
    {
        public double centerLat;
        public double centerLon;
        public float radiusDeg;
        public float textureHalfSizeDeg;
        public string texture;
        public List<Label> labels;
    }

    private class Label
    {
        public string name;
        public float x;
        public float y;
        public int rank;
    }

    private readonly GeoMapProjection projection = new GeoMapProjection();
    private readonly Dictionary<GraphNode, Vector2> planar = new Dictionary<GraphNode, Vector2>();
    private readonly HashSet<GraphNode> outside = new HashSet<GraphNode>();
    private MapFile map;
    private Texture2D texture;
    private Texture2D globeTexture;
    private bool globeLoaded;
    private readonly List<Object> owned = new List<Object>();
    private Transform visual;
    private InputAction toggleAction;
    private Coroutine transition;
    private readonly List<Rect> nameBoxes = new List<Rect>();
    private float placedFloorY;
    private float placedGraphY;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (cities == null) cities = FindFirstObjectByType<CityClusters>();
        toggleAction = new InputAction("Toggle Map View", InputActionType.Button);
        if (!string.IsNullOrEmpty(toggleBinding)) toggleAction.AddBinding(toggleBinding);
        toggleAction.AddBinding("<Keyboard>/m");
    }

    private void OnEnable() => toggleAction.Enable();

    private void OnDisable() => toggleAction.Disable();

    private void OnDestroy()
    {
        toggleAction.Dispose();
        if (graph != null && ReferenceEquals(graph.Layout, this)) graph.SetLayout(null);
        foreach (Object o in owned) if (o != null) Destroy(o);
        if (visual != null) Destroy(visual.gameObject);
    }

    private IEnumerator Start()
    {
        string json = null;
        yield return ReadStreamingAsset(mapFileName, bytes => json = bytes != null ? System.Text.Encoding.UTF8.GetString(bytes) : null);
        if (json == null) yield break;
        try
        {
            map = JsonConvert.DeserializeObject<MapFile>(json);
        }
        catch (JsonException e)
        {
            Debug.LogError($"GeoMapView: {mapFileName} is not valid JSON: {e.Message}");
            yield break;
        }
        if (map == null || map.radiusDeg <= 0f || map.textureHalfSizeDeg <= 0f)
        {
            Debug.LogError($"GeoMapView: {mapFileName} has no map radius / texture size.");
            yield break;
        }

        byte[] image = null;
        string textureFile = string.IsNullOrEmpty(textureOverride) ? map.texture : textureOverride;
        if (!string.IsNullOrEmpty(textureFile)) yield return ReadStreamingAsset(textureFile, bytes => image = bytes);
        if (image != null) texture = LoadTexture(image);

        while (graph == null || !graph.IsLoaded) yield return null;
        Place();
        PlaceAirports();
        BuildVisual();
        Ready = true;
        Debug.Log($"GeoMapView: map centre {map.centerLat:F2}N {map.centerLon:F2}E, radius {map.radiusDeg:F1} deg = {mapRadius:F2} m; " +
                  $"{planar.Count - outside.Count} airports on the map, {outside.Count} beyond it; texture " +
                  (texture != null ? $"{texture.width} px, {texture.mipmapCount} mips" : "missing"));
        if (startOnMap) SetMap(true, true);
    }

    private void Update()
    {
        if (!Ready) return;
        // The floor moves with GraphRecenter.matchEyeHeight (seated use): keep the map above it.
        if (!Switching && (Mathf.Abs(graph.FloorY - placedFloorY) > 0.005f ||
                           Mathf.Abs(graph.transform.position.y - placedGraphY) > 0.005f))
        {
            Replace();
        }
        if (GuidedTour.InputLocked)
        {
            // The tour's steps are written for the 3D layout.
            if (IsMap) SetMap(false, true);
            return;
        }
        if (toggleAction.WasPressedThisFrame()) Toggle();
    }

    // ---- Switching --------------------------------------------------------

    public void Toggle() => SetMap(!IsMap);

    /// <summary>Moves the airports onto the map (true) or back to the 3D layout (false), animated unless `instant`.</summary>
    public void SetMap(bool onMap, bool instant = false)
    {
        if (!Ready || (onMap == IsMap && transition == null)) return;
        IsMap = onMap;
        if (transition != null) StopCoroutine(transition);
        transition = StartCoroutine(Switch(onMap, instant));
    }

    private IEnumerator Switch(bool onMap, bool instant)
    {
        float fade = instant ? 0f : 0.2f;
        for (float t = 0f; t < fade; t += Time.deltaTime)
        {
            graph.SetEdgeFade(1f - t / fade);
            yield return null;
        }
        graph.SetEdgeFade(0f);

        var nodes = new List<GraphNode>(graph.Nodes.Values);
        var from = new Vector3[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) from[i] = nodes[i].transform.localPosition;
        if (onMap) ScaleCodes();
        graph.SetLayout(onMap ? this : null);
        var to = new Vector3[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) to[i] = cities != null ? cities.RestingPosition(nodes[i]) : graph.HomePosition(nodes[i]);

        if (onMap) visual.gameObject.SetActive(true);
        float seconds = instant ? 0f : transitionSeconds;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            float s = Mathf.SmoothStep(0f, 1f, t / seconds);
            for (int i = 0; i < nodes.Count; i++) nodes[i].transform.localPosition = Vector3.LerpUnclamped(from[i], to[i], s);
            // The map grows in as the airports arrive, and shrinks away as they leave.
            visual.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, onMap ? s : 1f - s);
            yield return null;
        }
        for (int i = 0; i < nodes.Count; i++) nodes[i].transform.localPosition = to[i];
        visual.localScale = Vector3.one;
        if (!onMap) visual.gameObject.SetActive(false);
        // B / the dashboard / the tour always bring the flat map next time.
        if (!onMap && IsDome) SetDomeShape(false, false); // ModeChanged below rebuilds what depends on the map

        graph.RebuildEdges(graph.Edges);
        float fadeIn = instant ? 0f : 0.35f;
        for (float t = 0f; t < fadeIn; t += Time.deltaTime)
        {
            graph.SetEdgeFade(t / fadeIn);
            yield return null;
        }
        graph.SetEdgeFade(1f);
        transition = null;
        ModeChanged?.Invoke(onMap);
    }

    // ---- IGraphLayout -----------------------------------------------------

    public Vector3 Home(GraphNode node)
    {
        return projection.Surface(PlanarOf(node), Lift);
    }

    public void FillEdge(Vector3 a, Vector3 b, Vector3[] points)
    {
        if (IsDome)
        {
            FillGreatCircle(a, b, points);
            return;
        }
        Transform t = graph.transform;
        Vector2 pa = projection.Unproject(t.InverseTransformPoint(a));
        Vector2 pb = projection.Unproject(t.InverseTransformPoint(b));
        float lift = Mathf.Min(maxArcLift * ViewScale, arcHeight * Vector2.Distance(pa, pb) * projection.metersPerDegree);
        projection.FillArc(pa, pb, Lift, lift, points);
        for (int i = 0; i < points.Length; i++) points[i] = t.TransformPoint(points[i]);
    }

    /// <summary>Airports' and routes' height off the surface: more on the dome (codes above them stay inside).</summary>
    private float Lift => IsDome ? domeNodeLift * ViewScale : nodeLift;

    /// <summary>Planar map position (degrees) of an airport: true for Europe, on the band for the rest.</summary>
    public Vector2 PlanarOf(GraphNode node)
    {
        return planar.TryGetValue(node, out Vector2 p) ? p : Vector2.zero;
    }

    // ---- Geometry ---------------------------------------------------------

    private float MetersToDegrees(float metres) => metres / projection.metersPerDegree;

    private void Place()
    {
        projection.centerLat = map.centerLat;
        projection.centerLon = map.centerLon;
        if (IsDome)
        {
            PlaceDome();
            return;
        }
        projection.metersPerDegree = mapRadius / map.radiusDeg;
        projection.curvatureRadius = curvatureRadius;
        float a = tilt * Mathf.Deg2Rad;
        projection.Place(new Vector3(0f, height, distance), new Vector3(0f, Mathf.Sin(a), -Mathf.Cos(a)), Vector3.up);

        // Tilted back and bent, the map's near rim hangs about a metre below its centre:
        // with the eyes at the graph centre (seated) that is under the floor.
        Transform t = graph.transform;
        float lowest = LowestPointY();
        float deficit = graph.FloorY + floorClearance - lowest;
        if (deficit > 0f)
        {
            projection.center += t.InverseTransformVector(Vector3.up * deficit);
            Debug.Log($"GeoMapView: map raised {deficit:F2} m to stay {floorClearance:F2} m above the floor (y = {graph.FloorY:F2}).");
        }
        placedFloorY = graph.FloorY;
        placedGraphY = t.position.y;
    }

    /// <summary>World height of the map's lowest point: the outer edge of the band, plus room for the names beyond it.</summary>
    private float LowestPointY()
    {
        float outer = map.radiusDeg + MetersToDegrees((bandGap + bandWidth + 0.06f) * ViewScale);
        float lowest = float.MaxValue;
        for (int j = 0; j < 72; j++)
        {
            float a = 2f * Mathf.PI * j / 72;
            Vector2 p = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * outer;
            lowest = Mathf.Min(lowest, graph.transform.TransformPoint(projection.Surface(p)).y);
        }
        return lowest;
    }

    /// <summary>Places the map again (floor or graph moved) and, when it moved, the airports and routes on it.</summary>
    private void Replace()
    {
        if (IsDome)
        {
            // Its size / height depend on the floor: shape it again, airports and routes with it.
            SetDomeShape(true);
            if (!IsMap) return;
            foreach (GraphNode node in graph.Nodes.Values)
            {
                node.transform.localPosition = cities != null ? cities.RestingPosition(node) : graph.HomePosition(node);
            }
            graph.RebuildEdges(graph.Edges);
            return;
        }
        Vector3 before = projection.center;
        Place();
        if ((projection.center - before).sqrMagnitude < 1e-8f) return;
        visual.localPosition = projection.center;
        Moved?.Invoke();
        if (!IsMap) return;
        foreach (GraphNode node in graph.Nodes.Values)
        {
            node.transform.localPosition = cities != null ? cities.RestingPosition(node) : graph.HomePosition(node);
        }
        graph.RebuildEdges(graph.Edges);
    }

    // ---- Dome -------------------------------------------------------------

    /// <summary>Switches between the flat map and the dome (map shown: the airports fly over; else only the shape changes).</summary>
    public void SetDome(bool dome)
    {
        if (!Ready || dome == IsDome || Switching) return;
        transition = StartCoroutine(ChangeShape(dome));
    }

    private IEnumerator ChangeShape(bool dome)
    {
        if (dome)
        {
            // The dome is centred on the graph centre: stand there first. Recentring may move the
            // floor (matchEyeHeight); GraphLoader reads the new FloorY in its LateUpdate, so wait.
            GraphRecenter recenter = FindFirstObjectByType<GraphRecenter>();
            if (recenter != null)
            {
                recenter.Recenter();
                yield return null;
                yield return null;
            }
        }
        if (dome && !globeLoaded)
        {
            // The world texture is only needed here: loaded the first time the dome opens.
            globeLoaded = true;
            byte[] image = null;
            if (!string.IsNullOrEmpty(globeTextureFile)) yield return ReadStreamingAsset(globeTextureFile, bytes => image = bytes);
            if (image != null)
            {
                globeTexture = LoadTexture(image);
                if (globeTexture != null) globeTexture.name = "Geo Globe Texture";
            }
        }
        SetDomeShape(dome);
        if (IsMap) yield return MoveToShape();
        transition = null;
    }

    /// <summary>Places the map (flat or dome), its airports and its visual again; raises Moved unless told not to.</summary>
    private void SetDomeShape(bool dome, bool raiseMoved = true)
    {
        IsDome = dome;
        scaleFrame = -1; // NodeScale depends on ViewScale
        ScaleCodes();
        Place();
        PlaceAirports();
        RebuildVisual();
        placedFloorY = graph.FloorY;
        placedGraphY = graph.transform.position.y;
        if (raiseMoved) Moved?.Invoke();
    }

    /// <summary>The airport codes (HubLabels, its public layoutLabelScale): hidden (scale 0) on the map and the dome unless showAirportCodes; else they grow with ViewScale on the dome.</summary>
    private void ScaleCodes()
    {
        HubLabels codes = FindFirstObjectByType<HubLabels>();
        if (codes == null) return;
        if (flatCodeScale < 0f) flatCodeScale = codes.layoutLabelScale;
        codes.layoutLabelScale = showAirportCodes ? flatCodeScale * ViewScale : 0f;
    }

    private float flatCodeScale = -1f;

    /// <summary>Dome: the projection on the inside of a sphere of domeRadius around the graph centre, map centre domeElevation up in front.</summary>
    private void PlaceDome()
    {
        // Azimuthal equidistant + this sphere = every point at its true angle from the map centre:
        // the view from the Earth's centre.
        projection.metersPerDegree = domeRadius * Mathf.Deg2Rad;
        // Negative curvature: the sphere's centre is on the viewer's side, at the graph centre.
        projection.curvatureRadius = -domeRadius;
        // Centre in front and up, facing you; north up, east right.
        float e = domeElevation * Mathf.Deg2Rad;
        Vector3 direction = new Vector3(0f, Mathf.Sin(e), Mathf.Cos(e));
        projection.Place(direction * domeRadius, -direction, Vector3.up);
        placedFloorY = graph.FloorY;
        placedGraphY = graph.transform.position.y;
        int hidden = 0;
        foreach (GraphNode node in graph.Nodes.Values)
        {
            Vector3 at = graph.transform.TransformPoint(projection.Surface(projection.Project(node.lat, node.lon), Lift));
            if (at.y < graph.FloorY + floorClearance) hidden++;
        }
        Debug.Log($"GeoMapView: dome {domeRadius:F1} m around you at true scale, Europe {domeElevation:F0} deg up" +
                  (hidden > 0 ? $"; {hidden} airport(s) below the floor" : "") + ".");
    }

    /// <summary>Dome: a route along the great circle between its airports, raised towards the centre in the middle.</summary>
    private void FillGreatCircle(Vector3 a, Vector3 b, Vector3[] points)
    {
        Transform t = graph.transform;
        Vector3 centre = projection.center - projection.normal * projection.curvatureRadius; // the sphere's centre (you)
        Vector3 da = (t.InverseTransformPoint(a) - centre).normalized;
        Vector3 db = (t.InverseTransformPoint(b) - centre).normalized;
        float angle = Vector3.Angle(da, db) * Mathf.Deg2Rad;
        float lift = Mathf.Min(maxArcLift * ViewScale, arcHeight * angle * domeRadius);
        int n = points.Length - 1;
        for (int i = 0; i <= n; i++)
        {
            float f = n > 0 ? (float)i / n : 0f;
            float r = domeRadius - Lift - lift * 4f * f * (1f - f);
            points[i] = t.TransformPoint(centre + Vector3.Slerp(da, db, f) * r);
        }
    }

    /// <summary>Destroys and builds the map's meshes, materials and names again (the texture stays).</summary>
    private void RebuildVisual()
    {
        bool shown = visual != null && visual.gameObject.activeSelf;
        if (visual != null) Destroy(visual.gameObject);
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            if (owned[i] == texture || owned[i] == globeTexture) continue;
            if (owned[i] != null) Destroy(owned[i]);
            owned.RemoveAt(i);
        }
        BuildVisual();
        visual.gameObject.SetActive(shown);
    }

    /// <summary>Map shown: the airports fly to their places on the new shape, routes fade out and in.</summary>
    private IEnumerator MoveToShape()
    {
        graph.SetEdgeFade(0f);
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        var from = new Vector3[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) from[i] = nodes[i].transform.localPosition;
        graph.SetLayout(this);
        var to = new Vector3[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) to[i] = cities != null ? cities.RestingPosition(nodes[i]) : graph.HomePosition(nodes[i]);
        for (float t = 0f; t < transitionSeconds; t += Time.deltaTime)
        {
            float s = Mathf.SmoothStep(0f, 1f, t / transitionSeconds);
            for (int i = 0; i < nodes.Count; i++) nodes[i].transform.localPosition = Vector3.LerpUnclamped(from[i], to[i], s);
            yield return null;
        }
        for (int i = 0; i < nodes.Count; i++) nodes[i].transform.localPosition = to[i];
        graph.RebuildEdges(graph.Edges);
        for (float t = 0f; t < 0.35f; t += Time.deltaTime)
        {
            graph.SetEdgeFade(t / 0.35f);
            yield return null;
        }
        graph.SetEdgeFade(1f);
        Moved?.Invoke();
    }

    private float BandMiddle => map.radiusDeg + MetersToDegrees((bandGap + bandWidth * 0.5f) * ViewScale);

    private void PlaceAirports()
    {
        planar.Clear();
        outside.Clear();
        if (IsDome)
        {
            // Every airport at its true place on the globe: nothing is "beyond the map".
            foreach (GraphNode node in graph.Nodes.Values) planar[node] = projection.Project(node.lat, node.lon);
            return;
        }
        var beyond = new List<GraphNode>();
        foreach (GraphNode node in graph.Nodes.Values)
        {
            Vector2 p = projection.Project(node.lat, node.lon);
            if (p.magnitude <= map.radiusDeg) planar[node] = p;
            else beyond.Add(node);
        }
        // Beyond the map: on the band in their true direction (bearing from north, clockwise),
        // spread where several share a direction (the US east coast, the Gulf).
        beyond.Sort((x, y) => Bearing(x).CompareTo(Bearing(y)));
        var bearings = new float[beyond.Count];
        for (int i = 0; i < beyond.Count; i++) bearings[i] = Bearing(beyond[i]);
        float ring = BandMiddle;
        float minGap = Mathf.Min(outsideSpacing * ViewScale / (ring * projection.metersPerDegree), 2f * Mathf.PI / Mathf.Max(1, beyond.Count));
        float[] angles = Spread(bearings, minGap);
        for (int i = 0; i < beyond.Count; i++)
        {
            planar[beyond[i]] = new Vector2(Mathf.Sin(angles[i]), Mathf.Cos(angles[i])) * ring;
            outside.Add(beyond[i]);
        }
    }

    /// <summary>
    /// Sorted angles moved as little as possible (least squares) so neighbours are at least
    /// `minGap` apart, keeping their order: runs that would overlap merge into one group,
    /// evenly spaced and centred on its members' mean.
    /// </summary>
    public static float[] Spread(float[] sorted, float minGap)
    {
        var first = new List<int>();
        var size = new List<int>();
        var sum = new List<float>();
        for (int i = 0; i < sorted.Length; i++)
        {
            first.Add(i);
            size.Add(1);
            sum.Add(sorted[i]);
            while (first.Count >= 2)
            {
                int b = first.Count - 1, a = b - 1;
                float aEnd = sum[a] / size[a] + (size[a] - 1) * 0.5f * minGap;
                float bStart = sum[b] / size[b] - (size[b] - 1) * 0.5f * minGap;
                if (bStart - aEnd >= minGap - 1e-6f) break;
                size[a] += size[b];
                sum[a] += sum[b];
                first.RemoveAt(b);
                size.RemoveAt(b);
                sum.RemoveAt(b);
            }
        }
        var angles = new float[sorted.Length];
        for (int c = 0; c < first.Count; c++)
        {
            float start = sum[c] / size[c] - (size[c] - 1) * 0.5f * minGap;
            for (int k = 0; k < size[c]; k++) angles[first[c] + k] = start + k * minGap;
        }
        return angles;
    }

    private float Bearing(GraphNode node)
    {
        Vector2 p = projection.Project(node.lat, node.lon);
        return Mathf.Atan2(p.x, p.y);
    }

    // ---- Visual -----------------------------------------------------------

    private void BuildVisual()
    {
        visual = new GameObject("Geo Map").transform;
        visual.SetParent(graph.transform, false);
        visual.localPosition = projection.center;   // meshes around the centre, so it scales in place

        float r = map.radiusDeg;
        float k = ViewScale;
        float bandFrom = r + MetersToDegrees(bandGap * k);
        float bandTo = bandFrom + MetersToDegrees(bandWidth * k);
        float rim = MetersToDegrees(0.006f * k);
        AddMesh("Map", Disc(r, 48, 160, map.textureHalfSizeDeg), TextureMaterial());
        if (!IsDome) AddMesh("Rim", Annulus(r, r + rim, 160, 0.001f), ColorMaterial(rimColor)); // on the dome Europe blends into the world
        if (IsDome)
        {
            AddGlobe();
        }
        else
        {
            AddMesh("Band", Annulus(bandFrom, bandTo, 160, 0f), ColorMaterial(bandColor));
            AddMesh("Band Edge", Annulus(bandTo, bandTo + rim * 0.5f, 160, 0.001f), ColorMaterial(rimColor * 0.5f));
        }

        nameBoxes.Clear();
        int hidden = 0;
        if (showCountryNames && map.labels != null)
        {
            // Most important countries first, so they keep their names.
            var countries = new List<Label>(map.labels);
            countries.Sort((a, b) => a.rank.CompareTo(b.rank));
            foreach (Label label in countries)
            {
                float size = Mathf.Lerp(0.34f, 0.18f, Mathf.InverseLerp(2f, 6f, label.rank)) * countryNameScale * k;
                Vector2 at = new Vector2(label.x, label.y);
                TextMeshPro text = NewText(label.name.ToUpperInvariant(), size, countryNameColor, at, 0.003f);
                text.characterSpacing = 6f;
                if (!KeepName(text, at)) hidden++;
            }
        }
        // Busiest airports first; with onePerCity a city's other airports get no name.
        // Dome: the airports beyond Europe, named next to their true place.
        var beyond = new List<GraphNode>(outside);
        if (IsDome) foreach (GraphNode node in graph.Nodes.Values) if (PlanarOf(node).magnitude > r) beyond.Add(node);
        beyond.Sort((a, b) => b.value.CompareTo(a.value));
        var named = new HashSet<string>();
        foreach (GraphNode node in beyond)
        {
            string city = node.data != null && !string.IsNullOrEmpty(node.data.city) ? node.data.city : node.label;
            if (onePerCity && !named.Add(city.ToLowerInvariant()))
            {
                hidden++;
                continue;
            }
            Vector2 dir = PlanarOf(node).normalized;
            Vector2 at = IsDome ? PlanarOf(node) + dir * MetersToDegrees(0.06f * k) : dir * (bandTo + MetersToDegrees(0.015f * k));
            TextMeshPro text = NewText(node.ShortCode + " · " + city, outsideNameSize * k, outsideNameColor, at, 0.003f);
            // Anchor the text on its side nearest the map, so it reads outwards from the band.
            Vector2 pivot = new Vector2(dir.x > 0.35f ? 0f : dir.x < -0.35f ? 1f : 0.5f,
                                        Mathf.Abs(dir.x) > 0.35f ? 0.5f : dir.y > 0f ? 0f : 1f);
            text.rectTransform.pivot = pivot;
            text.alignment = pivot.x == 0f ? TextAlignmentOptions.Left : pivot.x == 1f ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
            if (!KeepName(text, at)) hidden++;
        }
        if (hidden > 0) Debug.Log($"GeoMapView: {hidden} names left out so that the names on the map do not overlap.");
        visual.gameObject.SetActive(false);
    }

    /// <summary>
    /// Keeps the name unless (with hideOverlappingNames) its box on the map overlaps a name
    /// already kept; then it is removed. Boxes are in metres on the map (x east, y north).
    /// </summary>
    private bool KeepName(TextMeshPro text, Vector2 at)
    {
        if (!hideOverlappingNames) return true;
        Vector2 size = text.GetPreferredValues(text.text);
        Vector2 pivot = text.rectTransform.pivot;
        float boxHeight = text.rectTransform.sizeDelta.y;
        Vector2 c = at * projection.metersPerDegree;
        // The text is aligned to the pivot's side horizontally and centred vertically in its rect.
        float left = c.x - pivot.x * size.x;
        float middle = c.y + (0.5f - pivot.y) * boxHeight;
        float gap = nameGap * ViewScale;
        var box = new Rect(left - gap, middle - size.y * 0.5f - gap, size.x + 2f * gap, size.y + 2f * gap);
        foreach (Rect kept in nameBoxes)
        {
            if (!kept.Overlaps(box)) continue;
            Destroy(text.gameObject);
            return false;
        }
        nameBoxes.Add(box);
        return true;
    }

    private TextMeshPro NewText(string content, float fontSize, Color color, Vector2 at, float lift)
    {
        var go = new GameObject("Map Label " + content);
        go.transform.SetParent(visual, false);
        TextMeshPro text = go.AddComponent<TextMeshPro>();
        text.text = content;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.rectTransform.sizeDelta = new Vector2(0.6f, 0.06f) * ViewScale;
        go.transform.localPosition = projection.Surface(at, lift) - projection.center;
        // TMP text reads correctly when its +Z points away from the viewer: into the map.
        go.transform.localRotation = Quaternion.LookRotation(-projection.NormalAt(at), projection.NorthAt(at));
        return text;
    }

    private void AddMesh(string name, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(visual, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        owned.Add(mesh);
    }

    /// <summary>The map disc (radius in degrees), UVs over the texture's square of half size `half` degrees.</summary>
    private Mesh Disc(float radius, int rings, int segments, float half)
    {
        var vertices = new List<Vector3> { projection.Surface(Vector2.zero) - projection.center };
        var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
        var triangles = new List<int>();
        for (int k = 1; k <= rings; k++)
        {
            float rk = radius * k / rings;
            for (int j = 0; j < segments; j++)
            {
                float a = 2f * Mathf.PI * j / segments;
                Vector2 p = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * rk;
                vertices.Add(projection.Surface(p) - projection.center);
                uvs.Add(p / (2f * half) + new Vector2(0.5f, 0.5f));
            }
        }
        for (int j = 0; j < segments; j++) Face(triangles, 0, 1 + (j + 1) % segments, 1 + j);
        for (int k = 1; k < rings; k++)
        {
            int inner = 1 + (k - 1) * segments, outer = 1 + k * segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                Face(triangles, inner + j, inner + j2, outer + j2);
                Face(triangles, inner + j, outer + j2, outer + j);
            }
        }
        return NewMesh("Geo Map Disc", vertices, uvs, triangles);
    }

    /// <summary>A ring of the surface between two radii (degrees), raised `lift` metres.</summary>
    private Mesh Annulus(float from, float to, int segments, float lift)
    {
        const int steps = 3;
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        for (int k = 0; k <= steps; k++)
        {
            float rk = Mathf.Lerp(from, to, (float)k / steps);
            for (int j = 0; j < segments; j++)
            {
                float a = 2f * Mathf.PI * j / segments;
                Vector2 p = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * rk;
                vertices.Add(projection.Surface(p, lift) - projection.center);
                uvs.Add(Vector2.zero);
            }
        }
        for (int k = 0; k < steps; k++)
        {
            int inner = k * segments, outer = (k + 1) * segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                Face(triangles, inner + j, inner + j2, outer + j2);
                Face(triangles, inner + j, outer + j2, outer + j);
            }
        }
        return NewMesh("Geo Map Ring", vertices, uvs, triangles);
    }

    /// <summary>
    /// Dome: the rest of the globe — a latitude / longitude grid (equator and prime meridian
    /// brighter) just behind the map's surface and a dark sphere behind it, so the sky reads as
    /// the inside of the Earth. Real-width strips, not 1-pixel lines (they shimmer in VR).
    /// </summary>
    private void AddGlobe()
    {
        Vector3 centre = projection.center - projection.normal * projection.curvatureRadius;
        const float maxAngle = 178f; // the projection is singular at the antipode
        float width = 0.012f * ViewScale;
        if (graticuleStep > 0.5f)
        {
            var normal = new List<Vector3>();
            var main = new List<Vector3>();
            for (float lat = -90f + graticuleStep; lat < 90f - 0.01f; lat += graticuleStep)
            {
                GridLine(Mathf.Abs(lat) < 0.01f ? main : normal, i => new Vector2(lat, -180f + i * 2f), 181, centre, maxAngle, width);
            }
            for (float lon = -180f; lon < 180f - 0.01f; lon += graticuleStep)
            {
                GridLine(Mathf.Abs(lon) < 0.01f ? main : normal, i => new Vector2(-89.9f + i * 1.998f, lon), 91, centre, maxAngle, width);
            }
            AddMesh("Graticule", StripMesh("Geo Map Graticule", normal), ColorMaterial(graticuleColor));
            AddMesh("Graticule Main", StripMesh("Geo Map Graticule Main", main), ColorMaterial(graticuleMainColor));
        }
        if (globeTexture != null) AddMesh("Globe", WorldSphere(centre, domeRadius + 0.05f * ViewScale), TextureMaterial(globeTexture));
        else AddMesh("Globe Inside", Sphere(centre - projection.center, domeRadius + 0.15f * ViewScale, 48, 96), ColorMaterial(globeInsideColor));
    }

    // A line of (lat, lon) samples as quads on the globe just behind the surface, skipping the folded part.
    private void GridLine(List<Vector3> quads, System.Func<int, Vector2> latLon, int count, Vector3 centre, float maxAngle, float width)
    {
        Vector3 previous = Vector3.zero;
        bool hasPrevious = false;
        for (int i = 0; i < count; i++)
        {
            Vector2 ll = latLon(i);
            Vector2 p = projection.Project(ll.x, ll.y);
            if (p.magnitude > maxAngle)
            {
                hasPrevious = false;
                continue;
            }
            Vector3 at = projection.Surface(p, -0.03f * ViewScale) - projection.center;
            if (hasPrevious)
            {
                Vector3 radial = (at - (centre - projection.center)).normalized;
                Vector3 side = Vector3.Cross(at - previous, radial).normalized * (width * 0.5f);
                quads.Add(previous - side);
                quads.Add(previous + side);
                quads.Add(at + side);
                quads.Add(at - side);
            }
            previous = at;
            hasPrevious = true;
        }
    }

    private static Mesh StripMesh(string name, List<Vector3> quads)
    {
        var triangles = new List<int>();
        for (int i = 0; i + 3 < quads.Count; i += 4)
        {
            triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }
        var uvs = new List<Vector2>(new Vector2[quads.Count]);
        return NewMesh(name, quads, uvs, triangles);
    }

    /// <summary>
    /// Dome: the world texture (equirectangular) on a sphere of `radius` around `centre` (graph
    /// space), just behind the map of Europe. Each (lat, lon) goes to its true direction from the
    /// Earth's centre through a rotation (the map centre to projection.center's direction, east and
    /// north to the projection's), the same directions Surface(Project()) gives, but also fine at
    /// the antipode. A duplicated column at lon 180 keeps the texture seamless.
    /// </summary>
    private Mesh WorldSphere(Vector3 centre, float radius)
    {
        const int latSteps = 90, lonSteps = 180;
        double lat0 = map.centerLat * Mathf.Deg2Rad, lon0 = map.centerLon * Mathf.Deg2Rad;
        Vector3 Ecef(double la, double lo) => new Vector3((float)(System.Math.Cos(la) * System.Math.Cos(lo)),
                                                           (float)(System.Math.Cos(la) * System.Math.Sin(lo)),
                                                           (float)System.Math.Sin(la));
        Vector3 p0 = Ecef(lat0, lon0);
        Vector3 e0 = new Vector3((float)-System.Math.Sin(lon0), (float)System.Math.Cos(lon0), 0f);
        Vector3 n0 = Vector3.Cross(p0, e0); // north at the map centre
        Vector3 toCentre = -projection.normal; // from you towards the map centre
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        for (int i = 0; i <= latSteps; i++)
        {
            float lat = -90f + 180f * i / latSteps;
            for (int j = 0; j <= lonSteps; j++)
            {
                float lon = -180f + 360f * j / lonSteps;
                Vector3 v = Ecef(lat * Mathf.Deg2Rad, lon * Mathf.Deg2Rad);
                Vector3 dir = toCentre * Vector3.Dot(v, p0) + projection.east * Vector3.Dot(v, e0) + projection.north * Vector3.Dot(v, n0);
                vertices.Add(centre + dir * radius - projection.center);
                uvs.Add(new Vector2((lon + 180f) / 360f, (lat + 90f) / 180f));
            }
        }
        for (int i = 0; i < latSteps; i++)
        {
            for (int j = 0; j < lonSteps; j++)
            {
                int a = i * (lonSteps + 1) + j, b = a + lonSteps + 1;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }
        return NewMesh("Geo Map World Sphere", vertices, uvs, triangles);
    }

    // A UV sphere around `centre` (local), seen from inside (the materials draw both sides).
    private static Mesh Sphere(Vector3 centre, float radius, int rings, int segments)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int k = 0; k <= rings; k++)
        {
            float v = Mathf.PI * k / rings;
            for (int j = 0; j <= segments; j++)
            {
                float u = 2f * Mathf.PI * j / segments;
                vertices.Add(centre + new Vector3(Mathf.Sin(v) * Mathf.Cos(u), Mathf.Cos(v), Mathf.Sin(v) * Mathf.Sin(u)) * radius);
            }
        }
        for (int k = 0; k < rings; k++)
        {
            for (int j = 0; j < segments; j++)
            {
                int a = k * (segments + 1) + j, b = a + segments + 1;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }
        var uvs = new List<Vector2>(new Vector2[vertices.Count]);
        return NewMesh("Geo Map Globe Inside", vertices, uvs, triangles);
    }

    // Wound so the front faces the viewer (east = right, north = up as seen from in front).
    private static void Face(List<int> triangles, int a, int b, int c)
    {
        triangles.Add(a);
        triangles.Add(c);
        triangles.Add(b);
    }

    private static Mesh NewMesh(string name, List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        var mesh = new Mesh { name = name };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private Material TextureMaterial() => TextureMaterial(texture);

    private Material TextureMaterial(Texture2D tex)
    {
        Material m;
        if (mapMaterial != null) m = new Material(mapMaterial);
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            m = new Material(shader);
        }
        m.name = "Geo Map (runtime)";
        if (tex != null)
        {
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            m.mainTexture = tex;
        }
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // visible from behind too
        owned.Add(m);
        return m;
    }

    private Material ColorMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var m = new Material(shader) { name = "Geo Map Colour (runtime)" };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        owned.Add(m);
        return m;
    }

    private Texture2D LoadTexture(byte[] bytes)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Geo Map Texture" };
        if (!tex.LoadImage(bytes))
        {
            Debug.LogError("GeoMapView: the map texture could not be decoded.");
            Destroy(tex);
            return null;
        }
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;   // the map is seen at an angle
        tex.Compress(false);  // 4096 px: ~11 MB instead of ~64 MB on the GPU
        tex.Apply(false, true);
        owned.Add(tex);
        return tex;
    }

    private static IEnumerator ReadStreamingAsset(string fileName, System.Action<byte[]> onBytes)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        // Desktop / Editor: a bare path, UnityWebRequest needs a URI. Android: already jar:file://.
        string uri = path.Contains("://") ? path : "file://" + path;
        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"GeoMapView: '{fileName}' not found in StreamingAssets ({request.error}); " +
                                 "run scripts/build_geomap.py and scripts/sync_streaming_assets.sh. The map view is unavailable.");
                onBytes(null);
                yield break;
            }
            onBytes(request.downloadHandler.data);
        }
    }
}
