using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
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
/// per flight, so thousands of them stay cheap.
/// Shown and driven by FlightPanel (dashboard → Live flights, P in the Editor):
/// aircraft are drawn only while Visible.
/// Colours tell the flight phase from the data's flight levels: climbing, cruising,
/// descending (colorByPhase). Pointing at an aircraft and pulling the trigger (click in the
/// Editor; GraphSelector.RayClicked) selects it (TASKS B24): it turns yellow and larger,
/// its whole path is drawn, both airports light up and GraphInfoPanel shows a FLIGHT card.
/// </summary>
public class FlightSimulator : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Map view: aircraft fly at their real positions over it. Found in the scene if unset.")]
    public GeoMapView geoMap;
    [Tooltip("Ray clicks for picking aircraft, airport highlight. Found in the scene if unset.")]
    public GraphSelector selector;
    [Tooltip("Shows the selected aircraft's card. Found in the scene if unset.")]
    public GraphInfoPanel infoPanel;
    [Tooltip("Relative to Application.streamingAssetsPath.")]
    public string flightsFileName = "flights_ectrl.json";

    public enum PathMode { Auto, Trajectory, EdgeArc }
    [Tooltip("Auto: Trajectory (real positions) on the map view, EdgeArc (along the route arcs) in the 3D layout.")]
    public PathMode pathMode = PathMode.Auto;

    [Header("Time")]
    [Tooltip("Simulated seconds per real second (60 = one minute per second).")]
    public float speed = 60f;
    [Tooltip("Speeds the panel's speed button steps through (simulated seconds per second).")]
    public float[] speedSteps = { 15f, 30f, 60f, 120f, 300f, 600f, 1800f };
    [Tooltip("Start playing as soon as the flights are loaded (they are drawn only while Visible).")]
    public bool playOnLoad = false;
    [Tooltip("Restart from the window start when the end is reached.")]
    public bool loop = true;

    [Header("Aircraft")]
    [Tooltip("Optional mesh pointing along +Z (e.g. an aircraft model). Default: a low-poly 3D airliner built at runtime.")]
    public Mesh aircraftMesh;
    [Tooltip("Material with 'Enable GPU Instancing' (e.g. URP/Unlit). Assign it so the shader is in builds; " +
             "if unset, URP Unlit is looked up by name. A runtime copy gets aircraftColor.")]
    public Material aircraftMaterial;
    [Tooltip("Shade the aircraft with the scene's light (URP Simple Lit, instanced) so their 3D shape shows; an Unlit aircraftMaterial is replaced by it. Off = the material as it is.")]
    public bool litAircraft = true;
    [Tooltip("Light and plain so the aircraft do not compete with the orange / blue airports.")]
    public Color aircraftColor = new Color(0.78f, 0.84f, 0.94f);
    [Tooltip("Aircraft length in the 3D layout (m).")]
    public float aircraftSize = 0.11f;
    [Tooltip("Aircraft length on the map (m); the map is closer and Europe is ~2 m wide.")]
    public float mapAircraftSize = 0.038f;
    [Tooltip("Map view: height of FL400 (40,000 ft) above the map (m). Exaggerated: true to scale it would be ~4 mm.")]
    public float mapCruiseHeight = 0.04f;

    [Header("Flight phase colours")]
    [Tooltip("Colour each aircraft by what it is doing: climbing, cruising (aircraftColor) or descending. Off = all aircraftColor.")]
    public bool colorByPhase = true;
    public Color climbColor = new Color(0.45f, 0.86f, 0.6f);
    public Color descendColor = new Color(0.78f, 0.62f, 1f);
    [Tooltip("Vertical speed (ft/min) beyond which an aircraft counts as climbing / descending instead of cruising.")]
    public float levelChangeFtPerMin = 300f;

    [Header("Selected aircraft")]
    [Tooltip("An aircraft counts as hit within this angle of the ray (degrees, seen from the hand / eye).")]
    public float pickAngle = 2.5f;
    public Color selectedColor = new Color(1f, 0.9f, 0.2f);
    [Tooltip("Size of the selected aircraft relative to the others.")]
    public float selectedScale = 1.8f;
    [Tooltip("Width of the selected aircraft's path line on the map (m); three times this in the 3D layout.")]
    public float pathWidth = 0.004f;
    [Tooltip("Optional material for the path line (vertex colours, e.g. Sprites/Default). If unset, Sprites/Default is created.")]
    public Material pathMaterial;

    /// <summary>What an aircraft is doing, from the flight levels of its path.</summary>
    public enum Phase { Climbing, Cruising, Descending }

    /// <summary>A flight now (TryGetState).</summary>
    public struct FlightState
    {
        public bool started;        // off-block passed
        public bool landed;         // arrival passed
        public bool airborne;       // between the first and last point above the ground
        public float flightLevel;   // hundreds of feet, interpolated
        public float groundSpeedKmh;
        public float verticalFtPerMin;
        public Phase phase;
        public float progress;      // 0 at off-block .. 1 at arrival
    }

    private const int MaxInstancesPerCall = 1023;
    private const float MinStepSqr = 1e-10f;

    /// <summary>Runtime state of one flight.</summary>
    private class SimFlight
    {
        public FlightData data;
        public float start;
        public float end;
        // In the air: from the first to the last point above the ground (taxi is not drawn).
        public float airStart;
        public float airEnd;
        // Trajectory mode: path points in the graph's local space and the travel direction at each,
        // and which points are beyond the map's rim (a segment with both ends there is not drawn).
        public Vector3[] local;
        public Vector3[] direction;
        public bool[] offMap;
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
    // One draw batch per phase (Phase order) plus the selected aircraft; runtime material copies.
    private readonly Material[] phaseMaterials = new Material[3];
    private readonly RenderParams[] phaseParams = new RenderParams[3];
    private Matrix4x4[][] phaseMatrices = { new Matrix4x4[0], new Matrix4x4[0], new Matrix4x4[0] };
    private readonly int[] phaseCounts = new int[3];
    private readonly int[] phaseAirborne = new int[3];
    private Material selectedMaterial;
    private RenderParams selectedParams;
    private readonly Matrix4x4[] selectedMatrix = new Matrix4x4[1];
    private bool selectedDrawn;
    // Aircraft drawn in the last frame, for picking.
    private SimFlight[] drawnFlights = new SimFlight[0];
    private Vector3[] drawnPositions = new Vector3[0];
    private int activeCount;
    private int airborneCount;

    private bool visible;
    private SimFlight selected;
    private string selectedId;
    // True while this class clears GraphSelector itself, so its own SelectionCleared does not count.
    private bool selecting;
    private LineRenderer pathLine;
    private Material runtimePathMaterial;


    /// <summary>Aircraft are drawn (and time runs) only while visible: FlightPanel open. Hiding clears the selected aircraft.</summary>
    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            if (!visible) ClearFlight();
        }
    }
    public bool IsReady => ready;
    /// <summary>Length of the simulated window (s).</summary>
    public int DurationSec => data != null ? data.durationSec : 0;
    /// <summary>Seconds per second now (speed).</summary>
    public float Speed => speed;
    public bool IsPlaying => playing;
    /// <summary>Seconds since the window start (FlightSimData.startEpoch).</summary>
    public float SimTime => simTime;
    public int FlightCount => flights.Count;
    /// <summary>Aircraft drawn in the last frame (in the air; on the map, over it).</summary>
    public int ActiveCount => activeCount;
    /// <summary>Aircraft in the air in the last frame, wherever they are (also beyond the map's rim).</summary>
    public int AirborneCount => airborneCount;
    /// <summary>Of AirborneCount, how many are in each phase in the last frame.</summary>
    public int AirborneIn(Phase phase) => phaseAirborne[(int)phase];
    /// <summary>The selected aircraft (TASKS B24), or null.</summary>
    public FlightData SelectedFlight => selected != null ? selected.data : null;
    /// <summary>Raised when an aircraft is selected, and with null when the selection ends.</summary>
    public event Action<FlightData> FlightSelected;
    /// <summary>Trajectory or EdgeArc once loaded (Auto resolved).</summary>
    public PathMode ActiveMode => activeMode;
    public IReadOnlyList<FlightData> Flights => data != null ? data.flights : null;
    public DateTime SimUtc => data == null ? default : DateTimeOffset.FromUnixTimeSeconds(data.startEpoch).UtcDateTime.AddSeconds(simTime);

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (geoMap == null) geoMap = FindFirstObjectByType<GeoMapView>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (infoPanel == null) infoPanel = FindFirstObjectByType<GraphInfoPanel>();
    }

    private void OnEnable()
    {
        if (geoMap != null)
        {
            geoMap.ModeChanged += OnMapModeChanged;
            geoMap.Moved += OnMapMoved;
        }
        if (selector != null)
        {
            selector.RayClicked += TryPick;
            selector.NodeSelected += OnGraphNodeSelected;
            selector.EdgeSelected += OnGraphEdgeSelected;
            selector.PathSelected += OnGraphPathSelected;
            selector.SelectionCleared += OnGraphSelectionCleared;
        }
    }

    private void OnDisable()
    {
        if (geoMap != null)
        {
            geoMap.ModeChanged -= OnMapModeChanged;
            geoMap.Moved -= OnMapMoved;
        }
        if (selector != null)
        {
            selector.RayClicked -= TryPick;
            selector.NodeSelected -= OnGraphNodeSelected;
            selector.EdgeSelected -= OnGraphEdgeSelected;
            selector.PathSelected -= OnGraphPathSelected;
            selector.SelectionCleared -= OnGraphSelectionCleared;
        }
    }

    // Another selection (airport, route, connection) or a cleared one ends the aircraft's.
    private void OnGraphNodeSelected(GraphNode node) => DropFlight();
    private void OnGraphEdgeSelected(GraphEdge edge) => DropFlight();
    private void OnGraphPathSelected(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges) => DropFlight();
    private void OnGraphSelectionCleared() => DropFlight();

    // Map <-> 3D: paths are rebuilt for the new layout (real positions or route arcs).
    private void OnMapModeChanged(bool onMap)
    {
        if (!ready) return;
        BuildFlights();
        ResetCursors();
    }

    // The map was raised above a moved floor (GraphRecenter seated use): the paths on it move along.
    private void OnMapMoved()
    {
        if (!ready || activeMode != PathMode.Trajectory) return;
        BuildFlights();
        ResetCursors();
    }

    private void OnDestroy()
    {
        // Runtime-created assets are not scene objects: destroy them or they leak every Play.
        foreach (Material m in phaseMaterials) if (m != null) Destroy(m);
        if (selectedMaterial != null) Destroy(selectedMaterial);
        if (runtimePathMaterial != null) Destroy(runtimePathMaterial);
        if (ownedMesh != null) Destroy(ownedMesh);
        if (pathLine != null) Destroy(pathLine.gameObject);
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

        // A whole day is ~25,000 flights (~11 MB): parse off the main thread, so the headset keeps its frame rate.
        string parseError = null;
        FlightSimData parsed = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var parse = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                parsed = JsonConvert.DeserializeObject<FlightSimData>(json);
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
            }
        });
        while (!parse.IsCompleted) yield return null;
        data = parsed;
        if (parse.IsFaulted) parseError = parse.Exception?.GetBaseException().Message;
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

        float parseSeconds = (float)watch.Elapsed.TotalSeconds;
        watch.Restart();
        BuildFlights();
        Debug.Log($"FlightSimulator: parsed in {parseSeconds:F2} s (background), paths built in {watch.Elapsed.TotalSeconds:F2} s");
        if (!CreateRenderResources()) yield break;
        playing = playOnLoad;
        ready = true;
    }

    private void Update()
    {
        if (!ready || !Visible) return;

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
        if (geoMap != null && geoMap.Switching)
        {
            if (pathLine != null) pathLine.enabled = false;
            return;
        }
        ApplyColours();
        UpdateInstances();
        for (int p = 0; p < 3; p++)
        {
            for (int start = 0; start < phaseCounts[p]; start += MaxInstancesPerCall)
            {
                Graphics.RenderMeshInstanced(phaseParams[p], mesh, 0, phaseMatrices[p], Mathf.Min(MaxInstancesPerCall, phaseCounts[p] - start), start);
            }
        }
        if (selectedDrawn) Graphics.RenderMeshInstanced(selectedParams, mesh, 0, selectedMatrix, 1);
        UpdatePathLine();
    }

    // ------------------------------------------------------------------ public controls

    public void Play() { playing = true; }
    public void Pause() { playing = false; }
    public void TogglePlay() { playing = !playing; }

    /// <summary>Jumps to `seconds` from the window start.</summary>
    public void Seek(float seconds)
    {
        simTime = Mathf.Clamp(seconds, 0f, data != null ? data.durationSec : 0f);
        ResetCursors();
        lastSimTime = simTime;
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
                Debug.LogWarning($"FlightSimulator: '{fileName}' not found in StreamingAssets ({request.error}); " +
                                 "run scripts/build_flight_sim.py and sync it. Live flights are unavailable.");
                yield break;
            }
            onText(request.downloadHandler.text);
        }
    }

    /// <summary>Aircraft in the air (first .. last point above the ground) in `bins` equal slices of the window, for a chart.</summary>
    public float[] AirborneProfile(int bins)
    {
        var counts = new float[Mathf.Max(1, bins)];
        if (data == null || data.durationSec <= 0) return counts;
        float slice = (float)data.durationSec / counts.Length;
        foreach (SimFlight f in flights)
        {
            int a = Mathf.Clamp(Mathf.FloorToInt(f.airStart / slice), 0, counts.Length - 1);
            int b = Mathf.Clamp(Mathf.FloorToInt(f.airEnd / slice), 0, counts.Length - 1);
            if (f.airEnd < 0f || f.airStart > data.durationSec) continue;
            for (int i = a; i <= b; i++) counts[i]++;
        }
        return counts;
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
            AirborneSpan(f, out sim.airStart, out sim.airEnd);
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

        for (int p = 0; p < 3; p++) phaseMatrices[p] = new Matrix4x4[flights.Count];
        drawnFlights = new SimFlight[flights.Count];
        drawnPositions = new Vector3[flights.Count];
        // The selected aircraft survives a rebuild (map <-> 3D, map raised): the same flight, new paths.
        selected = selectedId != null && flightsById.TryGetValue(selectedId, out SimFlight again) ? again : null;
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
        sim.offMap = new bool[n];
        for (int i = 0; i < n; i++)
        {
            planar[i] = proj.Project(f.lat[i], f.lon[i]);
            sim.offMap[i] = planar[i].magnitude > radius;
            if (sim.offMap[i]) continue;
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

        Shader fallback = null;
        // Lit: an Unlit material shows the 3D mesh as a flat silhouette.
        Shader lit = litAircraft ? Shader.Find("Universal Render Pipeline/Simple Lit") : null;
        bool useLit = lit != null && (aircraftMaterial == null || aircraftMaterial.shader.name.Contains("Unlit"));
        if (useLit) fallback = lit;
        else if (aircraftMaterial == null)
        {
            fallback = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (fallback == null)
            {
                Debug.LogError("FlightSimulator: no Unlit shader found. Assign aircraftMaterial.");
                return false;
            }
        }
        // Instances move every frame; a huge box keeps them from being culled as a group.
        var bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
        Material NewMaterial(string name)
        {
            Material m = aircraftMaterial != null && !useLit ? new Material(aircraftMaterial) : new Material(fallback);
            m.name = "Flight Sim " + name + " (runtime)";
            m.enableInstancing = true;
            return m;
        }
        RenderParams Params(Material m) => new RenderParams(m)
        {
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            layer = gameObject.layer,
            worldBounds = bounds,
        };
        for (int p = 0; p < 3; p++)
        {
            phaseMaterials[p] = NewMaterial(((Phase)p).ToString());
            phaseParams[p] = Params(phaseMaterials[p]);
        }
        selectedMaterial = NewMaterial("Selected");
        selectedParams = Params(selectedMaterial);
        ApplyColours();
        return true;
    }

    /// <summary>The colour an aircraft in this phase is drawn in (cruising = aircraftColor).</summary>
    public Color PhaseColor(Phase phase)
    {
        return phase == Phase.Climbing ? climbColor : phase == Phase.Descending ? descendColor : aircraftColor;
    }

    // Material colours from the Inspector (colours and colorByPhase can change while playing).
    private void ApplyColours()
    {
        for (int p = 0; p < 3; p++)
        {
            if (phaseMaterials[p] != null) phaseMaterials[p].color = colorByPhase ? PhaseColor((Phase)p) : aircraftColor;
        }
        if (selectedMaterial != null) selectedMaterial.color = selectedColor;
    }

    /// <summary>
    /// Default aircraft, 1 m long (scaled by aircraftSize), nose along +Z, up +Y: a low-poly
    /// airliner — round fuselage with nose and tail cones, swept wings and tailplane with some
    /// thickness, two engines under the wings, a fin. Flat-shaded: every triangle has its own
    /// vertices and an outward normal, so a lit material shows its shape.
    /// </summary>
    private static Mesh BuildAircraftMesh()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();

        // One triangle, wound so its front (and normal) faces `outward`.
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, outward) < 0f)
            {
                (b, c) = (c, b);
                n = -n;
            }
            n.Normalize();
            int i = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            normals.Add(n);
            normals.Add(n);
            normals.Add(n);
            triangles.Add(i);
            triangles.Add(i + 1);
            triangles.Add(i + 2);
        }

        // A tube along Z through `rings` (z, radius), sides sides, centred on (x, y); radius 0 = a point.
        void Tube(float x, float y, (float z, float r)[] rings, int sides)
        {
            for (int k = 0; k + 1 < rings.Length; k++)
            {
                for (int j = 0; j < sides; j++)
                {
                    float a0 = 2f * Mathf.PI * j / sides, a1 = 2f * Mathf.PI * (j + 1) / sides;
                    Vector3 Ring(int ring, float a) => new Vector3(x + Mathf.Cos(a) * rings[ring].r, y + Mathf.Sin(a) * rings[ring].r, rings[ring].z);
                    Vector3 p00 = Ring(k, a0), p01 = Ring(k, a1), p10 = Ring(k + 1, a0), p11 = Ring(k + 1, a1);
                    Vector3 outward = new Vector3(Mathf.Cos((a0 + a1) * 0.5f), Mathf.Sin((a0 + a1) * 0.5f), 0f);
                    if (rings[k].r > 0f) Tri(p00, p01, p11, outward);
                    if (rings[k + 1].r > 0f) Tri(p00, p11, p10, outward);
                    else Tri(p00, p01, p10, outward);
                }
            }
        }

        // A thin plate: outline in its own plane (u, v), `axis` = 1 for horizontal (y = level),
        // 0 for vertical (x = level); thickness t. Top, bottom and the edges.
        void Plate(Vector2[] outline, float level, float t, bool horizontal)
        {
            Vector3 At(Vector2 p, float side) => horizontal
                ? new Vector3(p.x, level + side * t * 0.5f, p.y)
                : new Vector3(level + side * t * 0.5f, p.x, p.y);
            Vector3 up = horizontal ? Vector3.up : Vector3.right;
            for (int i = 1; i + 1 < outline.Length; i++)
            {
                Tri(At(outline[0], 1f), At(outline[i], 1f), At(outline[i + 1], 1f), up);
                Tri(At(outline[0], -1f), At(outline[i], -1f), At(outline[i + 1], -1f), -up);
            }
            Vector2 centre = Vector2.zero;
            foreach (Vector2 p in outline) centre += p;
            centre /= outline.Length;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                Vector2 mid = (a + b) * 0.5f - centre;
                Vector3 outward = horizontal ? new Vector3(mid.x, 0f, mid.y) : new Vector3(0f, mid.x, mid.y);
                Tri(At(a, 1f), At(b, 1f), At(b, -1f), outward);
                Tri(At(a, 1f), At(b, -1f), At(a, -1f), outward);
            }
        }

        // Fuselage: nose cone, cabin, tail cone (slightly raised, as on airliners).
        Tube(0f, 0f, new[] { (0.5f, 0f), (0.46f, 0.03f), (0.4f, 0.05f), (0.3f, 0.058f), (-0.25f, 0.058f), (-0.4f, 0.04f), (-0.5f, 0f) }, 10);
        // Wings (low, swept back) and tailplane.
        Plate(new[] { new Vector2(0.04f, 0.14f), new Vector2(0.5f, -0.1f), new Vector2(0.5f, -0.16f), new Vector2(0.04f, -0.08f) }, -0.025f, 0.016f, true);
        Plate(new[] { new Vector2(-0.04f, 0.14f), new Vector2(-0.04f, -0.08f), new Vector2(-0.5f, -0.16f), new Vector2(-0.5f, -0.1f) }, -0.025f, 0.016f, true);
        Plate(new[] { new Vector2(0.03f, -0.33f), new Vector2(0.19f, -0.43f), new Vector2(0.19f, -0.48f), new Vector2(0.03f, -0.45f) }, 0.015f, 0.01f, true);
        Plate(new[] { new Vector2(-0.03f, -0.33f), new Vector2(-0.03f, -0.45f), new Vector2(-0.19f, -0.48f), new Vector2(-0.19f, -0.43f) }, 0.015f, 0.01f, true);
        // Fin (vertical; outline in (y, z)).
        Plate(new[] { new Vector2(0.03f, -0.26f), new Vector2(0.21f, -0.43f), new Vector2(0.21f, -0.49f), new Vector2(0.03f, -0.48f) }, 0f, 0.01f, false);
        // Engines under the wings.
        var engine = new[] { (0.13f, 0.022f), (0.12f, 0.03f), (-0.02f, 0.03f), (-0.05f, 0.02f) };
        Tube(0.19f, -0.065f, engine, 8);
        Tube(-0.19f, -0.065f, engine, 8);

        var mesh = new Mesh { name = "Flight Sim Aircraft" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
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
        airborneCount = 0;
        selectedDrawn = false;
        for (int p = 0; p < 3; p++)
        {
            phaseCounts[p] = 0;
            phaseAirborne[p] = 0;
        }
        bool map = activeMode == PathMode.Trajectory;
        Vector3 scale = Vector3.one * (map ? mapAircraftSize * geoMap.ViewScale : aircraftSize); // bigger on the dome
        Vector3 up = map ? mapUp : Vector3.up;
        foreach (SimFlight f in flights)
        {
            // Only aircraft in the air: taxiing ones would pile up on the hubs.
            if (simTime < f.airStart || simTime > f.airEnd) continue;
            airborneCount++;
            AdvanceCursor(f, simTime);
            Phase phase = PhaseOf(f.data, f.cursor);
            phaseAirborne[(int)phase]++;
            Pose(f, out Vector3 position, out Vector3 forward);
            // On the map, not while beyond its rim: they appear where they cross it.
            if (map && f.offMap[f.cursor] && f.offMap[Mathf.Min(f.cursor + 1, f.offMap.Length - 1)]) continue;
            Quaternion rotation = Quaternion.LookRotation(forward, up);
            drawnFlights[activeCount] = f;
            drawnPositions[activeCount] = position;
            activeCount++;
            if (f == selected)
            {
                selectedMatrix[0] = Matrix4x4.TRS(position, rotation, scale * selectedScale);
                selectedDrawn = true;
                continue;
            }
            int batch = colorByPhase ? (int)phase : (int)Phase.Cruising;
            phaseMatrices[batch][phaseCounts[batch]++] = Matrix4x4.TRS(position, rotation, scale);
        }
    }

    // Segment index at `time` (time only moves forward between ResetCursors).
    private static void AdvanceCursor(SimFlight f, float time)
    {
        int[] t = f.data.t;
        int last = t.Length - 2;
        while (f.cursor < last && t[f.cursor + 1] <= time) f.cursor++;
    }

    // Phase on segment i (point i to i + 1), from its vertical speed.
    private Phase PhaseOf(FlightData f, int i)
    {
        float rate = VerticalFtPerMin(f, i);
        return rate > levelChangeFtPerMin ? Phase.Climbing : rate < -levelChangeFtPerMin ? Phase.Descending : Phase.Cruising;
    }

    private static float VerticalFtPerMin(FlightData f, int i)
    {
        int j = Mathf.Min(i + 1, f.t.Length - 1);
        float minutes = (f.t[j] - f.t[i]) / 60f;
        return minutes > 0f ? (f.fl[j] - f.fl[i]) * 100f / minutes : 0f;
    }

    // First and last time the flight is above the ground (flight level > 0), widened to the
    // neighbouring ground points (take-off roll, landing); the whole flight if it never climbs.
    private static void AirborneSpan(FlightData f, out float from, out float to)
    {
        int n = f.t.Length;
        int first = -1, last = -1;
        for (int i = 0; i < n; i++)
        {
            if (f.fl[i] <= 0) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0)
        {
            from = f.t[0];
            to = f.t[n - 1];
            return;
        }
        from = f.t[Mathf.Max(0, first - 1)];
        to = f.t[Mathf.Min(n - 1, last + 1)];
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

    /// <summary>
    /// What flight `id` is doing at the current simulated time: flight level, ground speed and
    /// vertical speed on its current segment, phase, progress. False if the flight is unknown.
    /// </summary>
    public bool TryGetState(string id, out FlightState state)
    {
        state = default;
        if (id == null || !flightsById.TryGetValue(id, out SimFlight sim)) return false;
        FlightData f = sim.data;
        int n = f.t.Length;
        state.started = simTime >= sim.start;
        state.landed = simTime > sim.end;
        state.airborne = simTime >= sim.airStart && simTime <= sim.airEnd;
        state.progress = Mathf.InverseLerp(sim.start, sim.end, simTime);
        int i = 0;
        while (i < n - 2 && f.t[i + 1] <= simTime) i++;
        int j = i + 1;
        float span = f.t[j] - f.t[i];
        float u = span > 0f ? Mathf.Clamp01((simTime - f.t[i]) / span) : 1f;
        state.flightLevel = Mathf.Lerp(f.fl[i], f.fl[j], u);
        float hours = span / 3600f;
        state.groundSpeedKmh = hours > 0f ? DistanceKm(f.lat[i], f.lon[i], f.lat[j], f.lon[j]) / hours : 0f;
        state.verticalFtPerMin = VerticalFtPerMin(f, i);
        state.phase = PhaseOf(f, i);
        return true;
    }

    /// <summary>UTC time `seconds` after the window start (flight times: dep, arr, t).</summary>
    public DateTime UtcAt(float seconds)
    {
        return data == null ? default : DateTimeOffset.FromUnixTimeSeconds(data.startEpoch).UtcDateTime.AddSeconds(seconds);
    }

    private static float DistanceKm(float lat1, float lon1, float lat2, float lon2)
    {
        float p1 = lat1 * Mathf.Deg2Rad, p2 = lat2 * Mathf.Deg2Rad;
        float a = Mathf.Pow(Mathf.Sin((p2 - p1) / 2f), 2f) +
                  Mathf.Cos(p1) * Mathf.Cos(p2) * Mathf.Pow(Mathf.Sin((lon2 - lon1) * Mathf.Deg2Rad / 2f), 2f);
        return 2f * 6371f * Mathf.Asin(Mathf.Min(1f, Mathf.Sqrt(a)));
    }

    // ------------------------------------------------------------------ selected aircraft

    // GraphSelector.RayClicked: the drawn aircraft closest to the ray, within pickAngle or its own visible size.
    private bool TryPick(Ray ray)
    {
        if (!ready || !Visible || activeCount == 0 || GuidedTour.InputLocked) return false;
        SimFlight hit = null;
        float best = 1f; // angle / reach of the best hit so far; < 1 is a hit
        float half = (MapShown ? mapAircraftSize : aircraftSize) * 0.5f;
        for (int i = 0; i < activeCount; i++)
        {
            Vector3 to = drawnPositions[i] - ray.origin;
            if (to.sqrMagnitude < 0.01f) continue;
            // Within pickAngle of its centre, or anywhere on it when it is near and looks bigger.
            float reach = Mathf.Max(pickAngle, Mathf.Atan2(half, to.magnitude) * Mathf.Rad2Deg);
            float score = Vector3.Angle(ray.direction, to) / reach;
            if (score < best)
            {
                best = score;
                hit = drawnFlights[i];
            }
        }
        if (hit == null) return false;
        Select(hit);
        return true;
    }

    /// <summary>Selects flight `id` (as if picked); false if unknown.</summary>
    public bool SelectFlight(string id)
    {
        if (id == null || !flightsById.TryGetValue(id, out SimFlight f)) return false;
        Select(f);
        return true;
    }

    private void Select(SimFlight f)
    {
        // Clear the graph's own selection first (its card, highlight); not an end of ours.
        selecting = true;
        if (selector != null) selector.ClearSelection();
        selecting = false;
        selected = f;
        selectedId = f.data.id;
        if (selector != null && f.data.origin != null && f.data.destination != null &&
            graph.Nodes.TryGetValue(f.data.origin, out GraphNode from) && graph.Nodes.TryGetValue(f.data.destination, out GraphNode to))
        {
            selector.HighlightAirports(from, to);
        }
        if (infoPanel != null) infoPanel.ShowFlight(this, f.data);
        Camera cam = Camera.main;
        if (cam != null) UISounds.Play(UISounds.Click, cam.transform.position + cam.transform.forward * 0.5f);
        Debug.Log($"FlightSimulator: selected flight {f.data.id} {f.data.origin} -> {f.data.destination} ({f.data.op}, {f.data.type})");
        FlightSelected?.Invoke(f.data);
    }

    /// <summary>Ends the aircraft selection: its card, highlight and path go.</summary>
    public void ClearFlight()
    {
        if (selected == null) return;
        if (selector != null) selector.ClearSelection(); // also hides the card; calls DropFlight
        DropFlight();
    }

    // The selection ended elsewhere (GraphSelector): forget it, without touching GraphSelector.
    private void DropFlight()
    {
        if (selecting || selected == null) return;
        selected = null;
        selectedId = null;
        if (pathLine != null) pathLine.enabled = false;
        FlightSelected?.Invoke(null);
    }

    // The selected aircraft's whole path: its track on the map, its route arc in 3D.
    private void UpdatePathLine()
    {
        if (selected == null)
        {
            if (pathLine != null) pathLine.enabled = false;
            return;
        }
        if (pathLine == null)
        {
            var go = new GameObject("Selected Flight Path");
            pathLine = go.AddComponent<LineRenderer>();
            pathLine.useWorldSpace = true;
            pathLine.shadowCastingMode = ShadowCastingMode.Off;
            pathLine.receiveShadows = false;
            pathLine.numCapVertices = 2;
            if (pathMaterial != null) pathLine.sharedMaterial = pathMaterial;
            else
            {
                runtimePathMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Flight Sim Path (runtime)" };
                pathLine.sharedMaterial = runtimePathMaterial;
            }
        }
        bool map = activeMode == PathMode.Trajectory;
        pathLine.enabled = true;
        pathLine.widthMultiplier = map ? pathWidth : pathWidth * 3f;
        Color c = selectedColor;
        pathLine.startColor = pathLine.endColor = new Color(c.r, c.g, c.b, 0.85f);
        if (map)
        {
            Vector3[] local = selected.local;
            pathLine.positionCount = local.Length;
            for (int i = 0; i < local.Length; i++) pathLine.SetPosition(i, graph.transform.TransformPoint(local[i]));
        }
        else
        {
            Vector3[] points = selected.edge.points;
            pathLine.positionCount = points.Length;
            pathLine.SetPositions(points);
        }
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
}
