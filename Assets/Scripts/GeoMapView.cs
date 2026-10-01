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
/// </summary>
public class GeoMapView : MonoBehaviour, IGraphLayout
{
    public GraphLoader graph;
    [Tooltip("Airports collapsed into a city land on the city's spot. Found in the scene if unset.")]
    public CityClusters cities;

    public enum MapStyle { Dark, Satellite }

    [Header("Files (StreamingAssets)")]
    public string mapFileName = "geomap.json";
    [Tooltip("Dark: drawn map (coastlines, borders). Satellite: NASA Blue Marble imagery. Can be changed while playing.")]
    public MapStyle style = MapStyle.Dark;
    [Tooltip("Texture used instead of the style's (same projection as build_geomap.py). Empty = the style's.")]
    public string textureOverride = "";

    [Header("Placement (from the graph centre: your eyes after Go to graph centre)")]
    [Tooltip("Distance of the map centre in front of the graph centre (m).")]
    public float distance = 1.8f;
    [Tooltip("Height of the map centre relative to the graph centre (m).")]
    public float height = -0.35f;
    [Tooltip("How far the map leans back (deg): 0 = upright like a wall, 90 = flat like a table.")]
    [Range(0f, 90f)] public float tilt = 30f;
    [Tooltip("Radius of the map of Europe on the surface (m).")]
    public float mapRadius = 1.0f;
    [Tooltip("Curvature (m): > 0 bulges towards you like part of a globe, < 0 curves around you, 0 = flat.")]
    public float curvatureRadius = 3.5f;

    [Header("Airports and routes on the map")]
    [Tooltip("Airport size multiplier on the map (smaller than in the 3D layout, the map is closer).")]
    public float nodeScale = 0.45f;
    [Tooltip("Route width multiplier on the map.")]
    public float edgeWidthScale = 0.6f;
    [Tooltip("Route arc height as a fraction of its length on the map.")]
    public float arcHeight = 0.22f;
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
    /// <summary>Raised when a switch has finished (true = map).</summary>
    public event System.Action<bool> ModeChanged;
    public GeoMapProjection Projection => projection;
    public int OutsideCount => outside.Count;
    public bool IsOutside(GraphNode node) => outside.Contains(node);

    // IGraphLayout
    public float NodeScale => nodeScale;
    public float EdgeWidthScale => edgeWidthScale;

    private class MapFile
    {
        public double centerLat;
        public double centerLon;
        public float radiusDeg;
        public float textureHalfSizeDeg;
        public string texture;
        public string satelliteTexture;
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
    private Material mapSurfaceMaterial;
    private MapStyle loadedStyle;
    private bool loadingStyle;
    private readonly List<Object> owned = new List<Object>();
    private Transform visual;
    private InputAction toggleAction;
    private Coroutine transition;

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
        loadedStyle = style;
        string textureFile = TextureFile(style);
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
        if (style != loadedStyle && !loadingStyle) StartCoroutine(ChangeStyle(style));
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

    private string TextureFile(MapStyle s)
    {
        if (!string.IsNullOrEmpty(textureOverride)) return textureOverride;
        if (s == MapStyle.Satellite) return string.IsNullOrEmpty(map.satelliteTexture) ? "geomap_satellite.jpg" : map.satelliteTexture;
        return map.texture;
    }

    // Loads the other style's texture and puts it on the map (the old one is freed).
    private IEnumerator ChangeStyle(MapStyle target)
    {
        loadingStyle = true;
        byte[] image = null;
        yield return ReadStreamingAsset(TextureFile(target), bytes => image = bytes);
        loadedStyle = target;
        loadingStyle = false;
        Texture2D loaded = image != null ? LoadTexture(image) : null;
        if (loaded == null) yield break;
        if (texture != null)
        {
            owned.Remove(texture);
            Destroy(texture);
        }
        texture = loaded;
        if (mapSurfaceMaterial != null)
        {
            if (mapSurfaceMaterial.HasProperty("_BaseMap")) mapSurfaceMaterial.SetTexture("_BaseMap", texture);
            mapSurfaceMaterial.mainTexture = texture;
        }
        Debug.Log($"GeoMapView: map style {target}");
    }

    // ---- IGraphLayout -----------------------------------------------------

    public Vector3 Home(GraphNode node)
    {
        return projection.Surface(PlanarOf(node), nodeLift);
    }

    public void FillEdge(Vector3 a, Vector3 b, Vector3[] points)
    {
        Transform t = graph.transform;
        Vector2 pa = projection.Unproject(t.InverseTransformPoint(a));
        Vector2 pb = projection.Unproject(t.InverseTransformPoint(b));
        float lift = arcHeight * Vector2.Distance(pa, pb) * projection.metersPerDegree;
        projection.FillArc(pa, pb, nodeLift, lift, points);
        for (int i = 0; i < points.Length; i++) points[i] = t.TransformPoint(points[i]);
    }

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
        projection.metersPerDegree = mapRadius / map.radiusDeg;
        projection.curvatureRadius = curvatureRadius;
        float a = tilt * Mathf.Deg2Rad;
        projection.Place(new Vector3(0f, height, distance), new Vector3(0f, Mathf.Sin(a), -Mathf.Cos(a)), Vector3.up);
    }

    private float BandMiddle => map.radiusDeg + MetersToDegrees(bandGap + bandWidth * 0.5f);

    private void PlaceAirports()
    {
        planar.Clear();
        outside.Clear();
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
        float minGap = Mathf.Min(outsideSpacing / (ring * projection.metersPerDegree), 2f * Mathf.PI / Mathf.Max(1, beyond.Count));
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
        float bandFrom = r + MetersToDegrees(bandGap);
        float bandTo = bandFrom + MetersToDegrees(bandWidth);
        float rim = MetersToDegrees(0.006f);
        AddMesh("Map", Disc(r, 48, 160, map.textureHalfSizeDeg), TextureMaterial());
        AddMesh("Rim", Annulus(r, r + rim, 160, 0.001f), ColorMaterial(rimColor));
        AddMesh("Band", Annulus(bandFrom, bandTo, 160, 0f), ColorMaterial(bandColor));
        AddMesh("Band Edge", Annulus(bandTo, bandTo + rim * 0.5f, 160, 0.001f), ColorMaterial(rimColor * 0.5f));

        if (showCountryNames && map.labels != null)
        {
            foreach (Label label in map.labels)
            {
                float size = Mathf.Lerp(0.34f, 0.18f, Mathf.InverseLerp(2f, 6f, label.rank));
                TextMeshPro text = NewText(label.name.ToUpperInvariant(), size, countryNameColor, new Vector2(label.x, label.y), 0.003f);
                text.characterSpacing = 6f;
            }
        }
        foreach (GraphNode node in outside)
        {
            Vector2 dir = PlanarOf(node).normalized;
            Vector2 at = dir * (bandTo + MetersToDegrees(0.015f));
            string city = node.data != null && !string.IsNullOrEmpty(node.data.city) ? node.data.city : node.label;
            TextMeshPro text = NewText(node.ShortCode + " · " + city, 0.3f, outsideNameColor, at, 0.003f);
            // Anchor the text on its side nearest the map, so it reads outwards from the band.
            Vector2 pivot = new Vector2(dir.x > 0.35f ? 0f : dir.x < -0.35f ? 1f : 0.5f,
                                        Mathf.Abs(dir.x) > 0.35f ? 0.5f : dir.y > 0f ? 0f : 1f);
            text.rectTransform.pivot = pivot;
            text.alignment = pivot.x == 0f ? TextAlignmentOptions.Left : pivot.x == 1f ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
        }
        visual.gameObject.SetActive(false);
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
        text.rectTransform.sizeDelta = new Vector2(0.6f, 0.06f);
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

    private Material TextureMaterial()
    {
        Material m;
        if (mapMaterial != null) m = new Material(mapMaterial);
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            m = new Material(shader);
        }
        m.name = "Geo Map (runtime)";
        if (texture != null)
        {
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
            m.mainTexture = texture;
        }
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // visible from behind too
        owned.Add(m);
        mapSurfaceMaterial = m;
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
