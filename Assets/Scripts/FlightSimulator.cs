using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;

/// <summary>
/// Plays flights_ectrl.json (scripts/build_flight_sim.py) on top of the graph: one
/// small arrow per flight, moving in simulated UTC time and pointing where it flies.
/// - Map view (GeoMapView): each aircraft follows its timed path (lat / lon / flight
///   level) at its real position over the map, with the map's projection; parts beyond
///   the map run along the outer band, to or from the airport's spot there.
/// - 3D layout (positions are not geographic): each aircraft flies along its route's
///   arc from origin to destination, same timing.
/// Follows the switch between the two.
/// Aircraft are drawn with GPU instancing (one draw call per 1023), no GameObject
/// per flight, so hundreds of them stay cheap on Quest.
/// Controls: B (right controller) or P = play / pause; Y (left controller) or ] =
/// faster (cycles through speedSteps); [ = slower.
/// </summary>
public class FlightSimulator : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Map view: aircraft fly at their real positions over it. Found in the scene if unset.")]
    public GeoMapView geoMap;
    [Tooltip("Relative to Application.streamingAssetsPath.")]
    public string flightsFileName = "flights_ectrl.json";

    public enum PathMode { Auto, Trajectory, EdgeArc }
    [Tooltip("Auto: Trajectory (real positions) on the map view, EdgeArc (along the route arcs) in the 3D layout.")]
    public PathMode pathMode = PathMode.Auto;

    [Header("Time")]
    [Tooltip("Simulated seconds per real second (60 = one minute per second).")]
    public float speed = 60f;
    public float[] speedSteps = { 15f, 30f, 60f, 120f, 300f, 600f };
    public bool playOnLoad = true;
    [Tooltip("Restart from the window start when the end is reached.")]
    public bool loop = true;

    [Header("Aircraft")]
    [Tooltip("Optional mesh pointing along +Z (e.g. an aircraft model). Default: a small flat aircraft silhouette built at runtime.")]
    public Mesh aircraftMesh;
    [Tooltip("Material with 'Enable GPU Instancing' (e.g. URP/Unlit). Assign it so the shader is in builds; " +
             "if unset, URP Unlit is looked up by name. A runtime copy gets aircraftColor.")]
    public Material aircraftMaterial;
    [Tooltip("Light and plain so the aircraft do not compete with the orange / blue airports.")]
    public Color aircraftColor = new Color(0.86f, 0.9f, 0.98f);
    [Tooltip("Aircraft length in the 3D layout (m).")]
    public float aircraftSize = 0.06f;
    [Tooltip("Aircraft length on the map (m); the map is closer and Europe is ~2 m wide.")]
    public float mapAircraftSize = 0.024f;
    [Tooltip("Map view: height of FL400 (40,000 ft) above the map (m). Exaggerated: true to scale it would be ~4 mm.")]
    public float mapCruiseHeight = 0.04f;

    [Header("Clock")]
    [Tooltip("Floating label (time, aircraft in the air, speed) that follows the viewer.")]
    public bool showClock = true;
    public float clockDistance = 1.2f;
    [Tooltip("Height relative to the eyes (negative = below).")]
    public float clockHeight = -0.35f;
    [Tooltip("TextMeshPro 3D font size; 10 = 1 m line height.")]
    public float clockFontSize = 0.5f;
    public Color clockColor = new Color(1f, 1f, 1f, 0.9f);

    private const int MaxInstancesPerCall = 1023;
    private const float MinStepSqr = 1e-10f;

    /// <summary>Runtime state of one flight.</summary>
    private class SimFlight
    {
        public FlightData data;
        public float start;
        public float end;
        // Trajectory mode: path points in the graph's local space and the travel direction at each.
        public Vector3[] local;
        public Vector3[] direction;
        public int cursor;
        // EdgeArc mode: the graph edge, whether the flight runs target -> source, arc lengths.
        public GraphEdge edge;
        public bool reversed;
        public float[] arcLength;
    }

    private readonly List<SimFlight> flights = new List<SimFlight>();
    private readonly Dictionary<string, SimFlight> flightsById = new Dictionary<string, SimFlight>();
    private FlightSimData data;
    private PathMode activeMode;
    // Map view: the map's normal (world), aircraft lie flat on it.
    private Vector3 mapUp = Vector3.up;
    private float simTime;
    private float lastSimTime;
    private bool playing;
    private bool ready;

    // Mesh drawn per aircraft: aircraftMesh, or ownedMesh built at runtime (only that one is destroyed).
    private Mesh mesh;
    private Mesh ownedMesh;
    private Material runtimeMaterial;
    private RenderParams renderParams;
    private Matrix4x4[] matrices = new Matrix4x4[0];
    private int activeCount;

    private TextMeshPro clock;
    private float nextClockUpdate;

    private InputAction playPauseButton;
    private InputAction fasterButton;

    public bool IsReady => ready;
    public bool IsPlaying => playing;
    /// <summary>Seconds since the window start (FlightSimData.startEpoch).</summary>
    public float SimTime => simTime;
    public int FlightCount => flights.Count;
    /// <summary>Aircraft drawn in the last frame (between off-block and arrival).</summary>
    public int ActiveCount => activeCount;
    /// <summary>Trajectory or EdgeArc once loaded (Auto resolved).</summary>
    public PathMode ActiveMode => activeMode;
    public IReadOnlyList<FlightData> Flights => data != null ? data.flights : null;
    public DateTime SimUtc => data == null ? default : DateTimeOffset.FromUnixTimeSeconds(data.startEpoch).UtcDateTime.AddSeconds(simTime);

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (geoMap == null) geoMap = FindFirstObjectByType<GeoMapView>();
        playPauseButton = new InputAction("Flight Sim Play/Pause", InputActionType.Button, "<XRController>{RightHand}/secondaryButton");
        fasterButton = new InputAction("Flight Sim Faster", InputActionType.Button, "<XRController>{LeftHand}/secondaryButton");
    }

    private void OnEnable()
    {
        playPauseButton.Enable();
        fasterButton.Enable();
        if (geoMap != null) geoMap.ModeChanged += OnMapModeChanged;
    }

    private void OnDisable()
    {
        playPauseButton.Disable();
        fasterButton.Disable();
        if (geoMap != null) geoMap.ModeChanged -= OnMapModeChanged;
    }

    // Map <-> 3D: paths are rebuilt for the new layout (real positions or route arcs).
    private void OnMapModeChanged(bool onMap)
    {
        if (!ready) return;
        BuildFlights();
        ResetCursors();
    }

    private void OnDestroy()
    {
        playPauseButton.Dispose();
        fasterButton.Dispose();
        // Runtime-created assets are not scene objects: destroy them or they leak every Play.
        if (runtimeMaterial != null) Destroy(runtimeMaterial);
        if (ownedMesh != null) Destroy(ownedMesh);
        if (clock != null) Destroy(clock.gameObject);
    }

    private IEnumerator Start()
    {
        if (graph == null)
        {
            Debug.LogError("FlightSimulator: no GraphLoader in the scene.");
            yield break;
        }

        string json = null;
        yield return ReadStreamingAsset(flightsFileName, text => json = text);
        if (json == null) yield break; // error already logged

        // No yield inside try/catch: C# does not allow yield statements in catch blocks.
        string parseError = null;
        try
        {
            data = JsonConvert.DeserializeObject<FlightSimData>(json);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }
        if (parseError != null)
        {
            Debug.LogError($"FlightSimulator: JSON parse failed: {parseError}");
            yield break;
        }
        if (data == null || data.flights == null)
        {
            Debug.LogError("FlightSimulator: deserialized null (empty or malformed file).");
            yield break;
        }

        // Both modes need the loaded graph: its transform / scale or its edge arcs.
        while (!graph.IsLoaded) yield return null;

        BuildFlights();
        if (!CreateRenderResources()) yield break;
        if (showClock) BuildClock();
        playing = playOnLoad;
        ready = true;
    }

    private void Update()
    {
        if (!ready) return;
        HandleInput();

        if (playing)
        {
            simTime += Time.deltaTime * speed;
            if (simTime > data.durationSec)
            {
                if (loop)
                {
                    simTime = 0f;
                }
                else
                {
                    simTime = data.durationSec;
                    playing = false;
                }
            }
        }
        if (simTime < lastSimTime) ResetCursors(); // looped or seeked back
        lastSimTime = simTime;

        // While the airports fly between the 3D layout and the map, the paths do not fit either.
        if (geoMap != null && geoMap.Switching) return;
        UpdateInstances();
        for (int start = 0; start < activeCount; start += MaxInstancesPerCall)
        {
            Graphics.RenderMeshInstanced(renderParams, mesh, 0, matrices, Mathf.Min(MaxInstancesPerCall, activeCount - start), start);
        }
    }

    private void LateUpdate()
    {
        if (ready && clock != null) UpdateClock();
    }

    // ------------------------------------------------------------------ public controls

    public void Play() { playing = true; }
    public void Pause() { playing = false; }
    public void TogglePlay() { playing = !playing; nextClockUpdate = 0f; }

    /// <summary>Jumps to `seconds` from the window start.</summary>
    public void Seek(float seconds)
    {
        simTime = Mathf.Clamp(seconds, 0f, data != null ? data.durationSec : 0f);
        ResetCursors();
        lastSimTime = simTime;
        nextClockUpdate = 0f;
    }

    /// <summary>Next (+1) or previous (-1) entry of speedSteps; wraps around.</summary>
    public void StepSpeed(int direction)
    {
        if (speedSteps == null || speedSteps.Length == 0) return;
        int current = 0;
        for (int i = 0; i < speedSteps.Length; i++)
        {
            if (Mathf.Abs(speedSteps[i] - speed) < Mathf.Abs(speedSteps[current] - speed)) current = i;
        }
        int next = (current + direction + speedSteps.Length) % speedSteps.Length;
        speed = speedSteps[next];
        nextClockUpdate = 0f;
    }

    private void HandleInput()
    {
        if (playPauseButton.WasPressedThisFrame()) TogglePlay();
        if (fasterButton.WasPressedThisFrame()) StepSpeed(+1);
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.pKey.wasPressedThisFrame) TogglePlay();
        if (kb.rightBracketKey.wasPressedThisFrame) StepSpeed(+1);
        if (kb.leftBracketKey.wasPressedThisFrame) StepSpeed(-1);
    }

    // ------------------------------------------------------------------ setup

    private IEnumerator ReadStreamingAsset(string fileName, Action<string> onText)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        // Same as GraphLoader: a URI on desktop, already jar:file:// on Android / Quest.
        string uri = path.Contains("://") ? path : "file://" + path;
        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"FlightSimulator: failed to read '{uri}': {request.error}");
                yield break;
            }
            onText(request.downloadHandler.text);
        }
    }

    private bool MapShown => geoMap != null && geoMap.Ready && geoMap.IsMap;

    private void BuildFlights()
    {
        flights.Clear();
        flightsById.Clear();
        activeMode = pathMode == PathMode.Auto ? (MapShown ? PathMode.Trajectory : PathMode.EdgeArc) : pathMode;
        if (activeMode == PathMode.Trajectory && !MapShown)
        {
            Debug.LogWarning("FlightSimulator: Trajectory needs the map view (GeoMapView); flying along the route arcs instead.");
            activeMode = PathMode.EdgeArc;
        }
        if (activeMode == PathMode.Trajectory) mapUp = graph.transform.TransformDirection(geoMap.Projection.normal);

        var arcLengths = new Dictionary<GraphEdge, float[]>();
        int malformed = 0;
        int noEdge = 0;
        foreach (FlightData f in data.flights)
        {
            int n = f.t != null ? f.t.Length : 0;
            if (n < 2 || f.lat == null || f.lon == null || f.fl == null ||
                f.lat.Length != n || f.lon.Length != n || f.fl.Length != n)
            {
                malformed++;
                continue;
            }

            var sim = new SimFlight { data = f, start = f.t[0], end = f.t[n - 1] };
            if (activeMode == PathMode.Trajectory)
            {
                BuildTrajectory(sim);
            }
            else
            {
                sim.edge = FindEdge(f.origin, f.destination);
                if (sim.edge == null || sim.edge.points == null || sim.edge.points.Length < 2)
                {
                    noEdge++;
                    continue;
                }
                sim.reversed = sim.edge.sourceId != f.origin;
                if (!arcLengths.TryGetValue(sim.edge, out sim.arcLength))
                {
                    sim.arcLength = CumulativeLengths(sim.edge.points);
                    arcLengths.Add(sim.edge, sim.arcLength);
                }
            }
            flights.Add(sim);
            if (f.id != null) flightsById[f.id] = sim;
        }

        matrices = new Matrix4x4[flights.Count];
        Debug.Log($"FlightSimulator: {flights.Count} flights, {activeMode} mode" +
                  (malformed > 0 ? $", {malformed} malformed skipped" : "") +
                  (noEdge > 0 ? $", {noEdge} without a graph edge skipped" : ""));
    }

    /// <summary>
    /// Path points in the graph's local space, on the map (GeoMapView's projection), and
    /// travel directions. Beyond the map's rim a flight runs along the outer band: from its
    /// airport's spot there to where it enters the map (and back out the same way).
    /// </summary>
    private void BuildTrajectory(SimFlight sim)
    {
        FlightData f = sim.data;
        int n = f.t.Length;
        GeoMapProjection proj = geoMap.Projection;
        float radius = geoMap.MapRadiusDegrees;
        var planar = new Vector2[n];
        int firstOnMap = -1;
        int lastOnMap = -1;
        for (int i = 0; i < n; i++)
        {
            planar[i] = proj.Project(f.lat[i], f.lon[i]);
            if (planar[i].magnitude > radius) continue;
            if (firstOnMap < 0) firstOnMap = i;
            lastOnMap = i;
        }
        Vector2 startSpot = EndSpot(f.origin, planar[0], radius);
        Vector2 endSpot = EndSpot(f.destination, planar[n - 1], radius);
        if (firstOnMap < 0)
        {
            // Never over the map: straight from one end's spot to the other's, in time.
            for (int i = 0; i < n; i++) planar[i] = Vector2.Lerp(startSpot, endSpot, Progress(f.t, 0, n - 1, i));
        }
        else
        {
            Vector2 entry = Rim(planar[firstOnMap], radius);
            Vector2 exit = Rim(planar[lastOnMap], radius);
            for (int i = 0; i < firstOnMap; i++) planar[i] = Vector2.Lerp(startSpot, entry, Progress(f.t, 0, firstOnMap, i));
            for (int i = lastOnMap + 1; i < n; i++) planar[i] = Vector2.Lerp(exit, endSpot, Progress(f.t, lastOnMap, n - 1, i));
            for (int i = firstOnMap; i <= lastOnMap; i++) if (planar[i].magnitude > radius) planar[i] = Rim(planar[i], radius);
        }

        sim.local = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            sim.local[i] = proj.Surface(planar[i], geoMap.nodeLift + Mathf.Max(0, f.fl[i]) / 400f * mapCruiseHeight);
        }

        // Direction at point i: towards the next point that is somewhere else
        // (taxi / holding points repeat a position); the last points keep the one before.
        sim.direction = new Vector3[n];
        Vector3 last = Vector3.zero;
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = i + 1; j < n; j++)
            {
                Vector3 step = sim.local[j] - sim.local[i];
                if (step.sqrMagnitude > MinStepSqr)
                {
                    last = step.normalized;
                    break;
                }
            }
            sim.direction[i] = last;
        }
        Vector3 fallback = Vector3.forward;
        for (int i = 0; i < n; i++)
        {
            if (sim.direction[i] == Vector3.zero) sim.direction[i] = fallback;
            else fallback = sim.direction[i];
        }
    }

    // An airport beyond the map sits on the outer band: its spot there; otherwise the point itself, kept on the map.
    private Vector2 EndSpot(string airport, Vector2 point, float radius)
    {
        if (airport != null && graph.Nodes.TryGetValue(airport, out GraphNode node) && geoMap.IsOutside(node)) return geoMap.PlanarOf(node);
        return point.magnitude > radius ? Rim(point, radius) : point;
    }

    private static Vector2 Rim(Vector2 point, float radius)
    {
        return point.sqrMagnitude > 1e-12f ? point.normalized * radius : point;
    }

    // Share of the time from point a to point b reached at point i.
    private static float Progress(int[] t, int a, int b, int i)
    {
        float span = t[b] - t[a];
        return span > 0f ? Mathf.Clamp01((t[i] - t[a]) / span) : 1f;
    }

    private GraphEdge FindEdge(string a, string b)
    {
        foreach (GraphEdge e in graph.EdgesOf(a))
        {
            if ((e.sourceId == a && e.targetId == b) || (e.sourceId == b && e.targetId == a)) return e;
        }
        return null;
    }

    private static float[] CumulativeLengths(Vector3[] points)
    {
        var lengths = new float[points.Length];
        for (int i = 1; i < points.Length; i++)
        {
            lengths[i] = lengths[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }
        return lengths;
    }

    private bool CreateRenderResources()
    {
        if (aircraftMesh != null)
        {
            mesh = aircraftMesh;
        }
        else
        {
            ownedMesh = BuildAircraftMesh();
            mesh = ownedMesh;
        }

        Material source = aircraftMaterial;
        if (source != null)
        {
            runtimeMaterial = new Material(source);
        }
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                Debug.LogError("FlightSimulator: no Unlit shader found. Assign aircraftMaterial.");
                return false;
            }
            runtimeMaterial = new Material(shader);
        }
        runtimeMaterial.name = "Flight Sim Aircraft (runtime)";
        runtimeMaterial.enableInstancing = true;
        runtimeMaterial.color = aircraftColor;

        renderParams = new RenderParams(runtimeMaterial)
        {
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            layer = gameObject.layer,
            // Instances move every frame; a huge box keeps them from being culled as a group.
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f),
        };
        return true;
    }

    /// <summary>
    /// Unit-length aircraft silhouette along +Z, flat (seen from above like a map symbol):
    /// slim fuselage, swept wings, tailplane, plus a low fin so it does not vanish edge-on.
    /// Every face is present in both windings, so any culling mode shows it.
    /// </summary>
    private static Mesh BuildAircraftMesh()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        void Polygon(params Vector3[] points)
        {
            int start = vertices.Count;
            vertices.AddRange(points);
            for (int i = 1; i + 1 < points.Length; i++)
            {
                triangles.AddRange(new[] { start, start + i, start + i + 1, start, start + i + 1, start + i });
            }
        }
        Vector3 P(float x, float z) => new Vector3(x, 0f, z);
        // Fuselage (nose at +Z).
        Polygon(P(0f, 0.5f), P(0.045f, 0.4f), P(0.045f, -0.36f), P(0f, -0.5f), P(-0.045f, -0.36f), P(-0.045f, 0.4f));
        // Wings, swept back.
        Polygon(P(0.04f, 0.14f), P(0.48f, -0.1f), P(0.48f, -0.17f), P(0.04f, -0.06f));
        Polygon(P(-0.04f, 0.14f), P(-0.04f, -0.06f), P(-0.48f, -0.17f), P(-0.48f, -0.1f));
        // Tailplane.
        Polygon(P(0.03f, -0.33f), P(0.18f, -0.43f), P(0.18f, -0.48f), P(0.03f, -0.44f));
        Polygon(P(-0.03f, -0.33f), P(-0.03f, -0.44f), P(-0.18f, -0.48f), P(-0.18f, -0.43f));
        // Fin.
        Polygon(new Vector3(0f, 0f, -0.28f), new Vector3(0f, 0.13f, -0.46f), new Vector3(0f, 0f, -0.48f));

        var mesh = new Mesh { name = "Flight Sim Aircraft" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ------------------------------------------------------------------ per frame

    private void ResetCursors()
    {
        foreach (SimFlight f in flights) f.cursor = 0;
    }

    private void UpdateInstances()
    {
        activeCount = 0;
        Vector3 scale = Vector3.one * (activeMode == PathMode.Trajectory ? mapAircraftSize : aircraftSize);
        Vector3 up = activeMode == PathMode.Trajectory ? mapUp : Vector3.up;
        foreach (SimFlight f in flights)
        {
            if (simTime < f.start || simTime > f.end) continue;
            Pose(f, out Vector3 position, out Vector3 forward);
            matrices[activeCount++] = Matrix4x4.TRS(position, Quaternion.LookRotation(forward, up), scale);
        }
    }

    /// <summary>World position and travel direction of a flight at the current simulated time.</summary>
    private void Pose(SimFlight f, out Vector3 position, out Vector3 forward)
    {
        if (activeMode == PathMode.Trajectory)
        {
            SampleTrajectory(f, simTime, out Vector3 local, out Vector3 localForward);
            position = graph.transform.TransformPoint(local);
            forward = graph.transform.TransformDirection(localForward);
        }
        else
        {
            SampleEdge(f, simTime, out position, out forward);
        }
        if (forward.sqrMagnitude < 1e-12f) forward = Vector3.forward;
    }

    /// <summary>
    /// Where flight `id` is now and where it is heading (world space); false if the
    /// flight is unknown or not between off-block and arrival at the current time.
    /// </summary>
    public bool TryGetPose(string id, out Vector3 position, out Vector3 forward)
    {
        position = default;
        forward = default;
        if (id == null || !flightsById.TryGetValue(id, out SimFlight f)) return false;
        if (simTime < f.start || simTime > f.end) return false;
        Pose(f, out position, out forward);
        return true;
    }

    private static void SampleTrajectory(SimFlight f, float time, out Vector3 position, out Vector3 forward)
    {
        int[] t = f.data.t;
        int last = t.Length - 2;
        // Time only moves forward between resets, so the segment index only moves forward too.
        while (f.cursor < last && t[f.cursor + 1] <= time) f.cursor++;
        int i = f.cursor;
        float span = t[i + 1] - t[i];
        float u = span > 0f ? Mathf.Clamp01((time - t[i]) / span) : 1f;
        position = Vector3.Lerp(f.local[i], f.local[i + 1], u);
        forward = Vector3.Slerp(f.direction[i], f.direction[i + 1], u);
    }

    private static void SampleEdge(SimFlight f, float time, out Vector3 position, out Vector3 forward)
    {
        float progress = Mathf.InverseLerp(f.start, f.end, time);
        if (f.reversed) progress = 1f - progress;
        Vector3[] points = f.edge.points;
        float[] lengths = f.arcLength;
        float target = progress * lengths[lengths.Length - 1];

        // Binary search for the segment containing `target`.
        int lo = 0;
        int hi = lengths.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (lengths[mid] <= target) lo = mid;
            else hi = mid;
        }
        float segment = lengths[hi] - lengths[lo];
        float u = segment > 0f ? (target - lengths[lo]) / segment : 0f;
        position = Vector3.Lerp(points[lo], points[hi], u);
        forward = points[hi] - points[lo];
        if (f.reversed) forward = -forward;
    }

    // ------------------------------------------------------------------ clock

    private void BuildClock()
    {
        var go = new GameObject("Flight Sim Clock");
        clock = go.AddComponent<TextMeshPro>();
        clock.fontSize = clockFontSize;
        clock.color = clockColor;
        clock.alignment = TextAlignmentOptions.Center;
        clock.textWrappingMode = TextWrappingModes.NoWrap;
        clock.overflowMode = TextOverflowModes.Overflow;
        clock.rectTransform.sizeDelta = new Vector2(1f, 0.2f);
    }

    private void UpdateClock()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;

        // Lazy follow: stays in front of the viewer at a fixed height without being head-locked.
        Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        Vector3 target = head.position + flatForward.normalized * clockDistance + Vector3.up * clockHeight;
        Transform t = clock.transform;
        t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-3f * Time.deltaTime));
        // TMP text reads correctly when its +Z points away from the viewer.
        Vector3 away = t.position - head.position;
        if (away.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(away, Vector3.up);

        if (Time.unscaledTime < nextClockUpdate) return;
        nextClockUpdate = Time.unscaledTime + 0.25f;
        clock.text = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm} UTC  {0:dd MMM yyyy}\n{1} in the air  {2}",
            SimUtc, activeCount, playing ? $"{speed:0}x" : "PAUSED");
    }
}
