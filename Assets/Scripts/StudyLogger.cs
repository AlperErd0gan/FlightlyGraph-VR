using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// User study log (B21, for the DIS / HCII papers). One CSV per session in
/// Application.persistentDataPath: study_&lt;participant&gt;_&lt;condition&gt;_&lt;date&gt;_&lt;time&gt;.csv
/// (path logged). Columns: time_s, event, detail, view, period, x, y, z, yaw.
/// Events: session start / end, task start / end (experimenter keys F5 / F6, numbered),
/// selections (airport, route, aircraft, cleared, connection request), view changes (3D / map,
/// dome, regional / top routes), timeline (period, filters, layout, play), guided tour, comfort,
/// controller buttons, Gemini talking / speaking, and head position + yaw twice a second.
/// Participants are identified by code only (P01, P02, ...): launch argument
/// <c>-participant P03</c> or the field. No names, no audio, no transcripts.
/// Voice commands: the spoken text is not logged (no script is changed); the push-to-talk
/// presses and Gemini's speech show when the participant spoke and was answered.
/// Self-contained: adds itself when the scene has a GraphLoader, changes no other script.
/// Every run (exe or Editor Play) is its own session and file; untick <see cref="logInEditor"/> to skip the Editor.
/// </summary>
public class StudyLogger : MonoBehaviour
{
    [Tooltip("Participant code, e.g. P01. A '-participant P01' launch argument overrides it.")]
    public string participant = "P00";
    [Tooltip("Also write a log when playing in the Editor (on: every run, Editor or exe, gets its own file).")]
    public bool logInEditor = true;
    [Tooltip("Experimenter key: a task starts (numbered 1, 2, ...).")]
    public string taskStartKey = "<Keyboard>/f5";
    [Tooltip("Experimenter key: the running task ends.")]
    public string taskEndKey = "<Keyboard>/f6";
    [Tooltip("Seconds between head samples (0 = none).")]
    public float headInterval = 0.5f;
    [Tooltip("Head samples closer than this to the last one (m and degrees) are skipped.")]
    public float headMinMove = 0.02f;

    private const float FlushEvery = 5f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private string csvPath;
    private readonly StringBuilder pending = new StringBuilder();
    private float startTime;
    private float lastFlush;
    private int taskNumber;
    private bool taskRunning;
    private float taskStarted;

    private InputAction taskStart, taskEnd;
    private readonly List<InputAction> buttons = new List<InputAction>();
    private InputAction talk;

    private GraphLoader graph;
    private GraphSelector selector;
    private GeoMapView map;
    private GraphViewMode viewMode;
    private FlightSimulator flights;
    private GuidedTour tour;
    private GeminiLiveAssistant gemini;
    private TimelinePanel timeline;
    private float nextLookup;

    // Polled state, logged on change.
    private bool wasSpeaking, wasPlaying, wasFlightsPlaying, wasTour, wasRegional, wasDome, wasMap, wasTalking;
    private float nextHead;
    private Vector3 lastHeadPos;
    private float lastHeadYaw;
    private bool haveHead;
    private float pathLength;
    private bool ended;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<StudyLogger>() != null) return;
        if (FindFirstObjectByType<GraphLoader>() == null) return;
        new GameObject("StudyLogger").AddComponent<StudyLogger>();
    }

    private void Awake()
    {
        if (Application.isEditor && !logInEditor)
        {
            enabled = false;
            return;
        }
        ReadLaunchArguments();
        participant = Clean(participant);

        taskStart = new InputAction("Study Task Start", InputActionType.Button, taskStartKey);
        taskEnd = new InputAction("Study Task End", InputActionType.Button, taskEndKey);

        // Controller buttons, logged on press (which button, not what it did).
        AddButton("trigger_R", "<XRController>{RightHand}/triggerPressed");
        AddButton("trigger_L", "<XRController>{LeftHand}/triggerPressed");
        AddButton("grip_R", "<XRController>{RightHand}/gripPressed");
        AddButton("grip_L", "<XRController>{LeftHand}/gripPressed");
        AddButton("A", "<XRController>{RightHand}/primaryButton");
        AddButton("B", "<XRController>{RightHand}/secondaryButton");
        AddButton("X", "<XRController>{LeftHand}/primaryButton");
        AddButton("stick_L", "<XRController>{LeftHand}/{Primary2DAxisClick}");
        AddButton("stick_R", "<XRController>{RightHand}/{Primary2DAxisClick}");
        AddButton("mouse_left", "<Mouse>/leftButton");
    }

    private void OnEnable()
    {
        taskStart?.Enable();
        taskEnd?.Enable();
        foreach (InputAction a in buttons) a.Enable();
        talk?.Enable();
    }

    private void OnDisable()
    {
        taskStart?.Disable();
        taskEnd?.Disable();
        foreach (InputAction a in buttons) a.Disable();
        talk?.Disable();
    }

    private void Start()
    {
        if (!enabled) return;
        StartCsv();
        Log("session_start", $"{Application.productName} {Application.version}; scene {SceneManager.GetActiveScene().name}; " +
                             $"{Application.platform}; {StudyCondition.Current}");
        Subscribe();
    }

    private void OnDestroy()
    {
        EndSession();
        Unsubscribe();
        taskStart?.Dispose();
        taskEnd?.Dispose();
        foreach (InputAction a in buttons) a.Dispose();
        talk?.Dispose();
    }

    private void OnApplicationPause(bool paused)
    {
        if (csvPath == null) return;
        Log(paused ? "app_pause" : "app_resume", "");
        if (paused) Flush();
    }

    private void OnApplicationQuit() => EndSession();

    private void EndSession()
    {
        if (ended || csvPath == null) return;
        ended = true;
        if (taskRunning) EndTask("session ended");
        Log("session_end", $"path {pathLength.ToString("F1", Inv)} m");
        Flush();
    }

    private void Update()
    {
        if (csvPath == null) return;
        float now = Time.unscaledTime;
        FindContext(now);
        Subscribe();

        if (taskStart.WasPressedThisFrame()) StartTask();
        if (taskEnd.WasPressedThisFrame()) EndTask("");

        foreach (InputAction a in buttons)
        {
            if (a.WasPressedThisFrame()) Log("button", a.name);
        }
        if (talk != null)
        {
            bool talking = talk.IsPressed();
            if (talking != wasTalking)
            {
                wasTalking = talking;
                Log(talking ? "talk_start" : "talk_end", "");
            }
        }

        Poll();
        SampleHead(now);
        if (now - lastFlush >= FlushEvery) Flush();
    }

    // ---- Experimenter tasks ------------------------------------------------

    private void StartTask()
    {
        if (taskRunning) EndTask("next task");
        taskNumber++;
        taskRunning = true;
        taskStarted = Time.unscaledTime;
        Log("task_start", taskNumber.ToString(Inv));
    }

    private void EndTask(string note)
    {
        if (!taskRunning) return;
        taskRunning = false;
        float seconds = Time.unscaledTime - taskStarted;
        Log("task_end", $"{taskNumber.ToString(Inv)}; {seconds.ToString("F1", Inv)} s" + (note.Length > 0 ? "; " + note : ""));
    }

    // ---- State polled on change -------------------------------------------

    private void Poll()
    {
        if (gemini != null)
        {
            bool speaking = gemini.IsSpeaking;
            if (speaking != wasSpeaking)
            {
                wasSpeaking = speaking;
                Log(speaking ? "gemini_speaks" : "gemini_silent", "");
            }
        }
        if (timeline != null && timeline.IsPlaying != wasPlaying)
        {
            wasPlaying = timeline.IsPlaying;
            Log(wasPlaying ? "timeline_play" : "timeline_pause", PeriodText());
        }
        if (flights != null && flights.IsPlaying != wasFlightsPlaying)
        {
            wasFlightsPlaying = flights.IsPlaying;
            Log(wasFlightsPlaying ? "flights_play" : "flights_pause", flights.SimTime.ToString("F0", Inv) + " s");
        }
        if (tour != null && tour.IsRunning != wasTour)
        {
            wasTour = tour.IsRunning;
            Log(wasTour ? "tour_start" : "tour_end", "");
        }
        if (graph != null && graph.RegionalView != wasRegional)
        {
            wasRegional = graph.RegionalView;
            Log("view_mode", wasRegional ? "regional" : "top_routes");
        }
        if (map != null)
        {
            if (map.IsMap != wasMap)
            {
                wasMap = map.IsMap;
                Log("view", wasMap ? "map" : "3d");
            }
            if (map.IsDome != wasDome)
            {
                wasDome = map.IsDome;
                Log("dome", wasDome ? "on" : "off");
            }
        }
    }

    private string PeriodText()
    {
        if (graph == null || !graph.HasPeriod) return "";
        return $"{graph.Period.ToString(Inv)}/{graph.PeriodCount.ToString(Inv)}";
    }

    // ---- Head ---------------------------------------------------------------

    private void SampleHead(float now)
    {
        if (headInterval <= 0f || now < nextHead) return;
        nextHead = now + headInterval;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 p = cam.transform.position;
        float yaw = cam.transform.eulerAngles.y;
        if (haveHead)
        {
            float moved = Vector3.Distance(p, lastHeadPos);
            float turned = Mathf.Abs(Mathf.DeltaAngle(yaw, lastHeadYaw));
            if (moved < headMinMove && turned < 1f) return;
            pathLength += moved;
        }
        haveHead = true;
        lastHeadPos = p;
        lastHeadYaw = yaw;
        Log("head", "", p, yaw);
    }

    // ---- Events -------------------------------------------------------------

    private bool subscribed;
    private bool subscribedSelector, subscribedGraph, subscribedMap, subscribedFlights;

    private void Subscribe()
    {
        if (selector != null && !subscribedSelector)
        {
            subscribedSelector = true;
            selector.NodeSelected += OnNode;
            selector.EdgeSelected += OnEdge;
            selector.SelectionCleared += OnCleared;
            selector.PathSelected += OnPath;
            selector.ConnectionRequested += OnConnection;
        }
        if (graph != null && !subscribedGraph)
        {
            subscribedGraph = true;
            graph.FilterChanged += OnFilter;
            graph.PeriodChanged += OnPeriod;
            graph.LayoutChanged += OnLayout;
        }
        if (flights != null && !subscribedFlights)
        {
            subscribedFlights = true;
            flights.FlightSelected += OnFlight;
        }
        if (!subscribed)
        {
            subscribed = true;
            Comfort.Changed += OnComfort;
        }
    }

    private void Unsubscribe()
    {
        if (selector != null && subscribedSelector)
        {
            selector.NodeSelected -= OnNode;
            selector.EdgeSelected -= OnEdge;
            selector.SelectionCleared -= OnCleared;
            selector.PathSelected -= OnPath;
            selector.ConnectionRequested -= OnConnection;
        }
        if (graph != null && subscribedGraph)
        {
            graph.FilterChanged -= OnFilter;
            graph.PeriodChanged -= OnPeriod;
            graph.LayoutChanged -= OnLayout;
        }
        if (flights != null && subscribedFlights) flights.FlightSelected -= OnFlight;
        if (subscribed) Comfort.Changed -= OnComfort;
        subscribed = subscribedSelector = subscribedGraph = subscribedFlights = false;
    }

    private void OnNode(GraphNode n) => Log("select_airport", n != null ? $"{n.id} {n.label}" : "");
    private void OnEdge(GraphEdge e) => Log("select_route", e != null ? $"{e.sourceId}-{e.targetId} w{e.weight.ToString(Inv)}" : "");
    private void OnCleared() => Log("select_clear", "");
    private void OnPath(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges) =>
        Log("select_path", nodes != null && nodes.Count > 0 ? $"{nodes[0].id}..{nodes[nodes.Count - 1].id} ({nodes.Count.ToString(Inv)})" : "");
    private void OnConnection(GraphNode a, GraphNode b) =>
        Log("connection_request", a != null && b != null ? $"{a.id}-{b.id}" : "");
    private void OnFlight(FlightData f) =>
        Log(f != null ? "select_flight" : "flight_clear", f != null ? $"{f.id} {f.origin}-{f.destination}" : "");
    private void OnFilter()
    {
        if (graph == null) return;
        Log("filter", $"segment={graph.FilterSegment ?? "-"}; country={graph.FilterCountry ?? "-"}; min_weight={graph.FilterMinWeight.ToString(Inv)}");
    }
    private void OnPeriod() => Log("period", PeriodText());
    private void OnLayout() => Log("layout", "");
    private void OnComfort() => Log("comfort", "reduce_motion=" + (Comfort.ReduceMotion ? "1" : "0"));

    // ---- Context -------------------------------------------------------------

    private void FindContext(float now)
    {
        if (now < nextLookup) return;
        nextLookup = now + 2f;
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (map == null) map = FindFirstObjectByType<GeoMapView>();
        if (viewMode == null) viewMode = FindFirstObjectByType<GraphViewMode>();
        if (flights == null) flights = FindFirstObjectByType<FlightSimulator>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (timeline == null) timeline = FindFirstObjectByType<TimelinePanel>();
        if (gemini == null)
        {
            gemini = FindFirstObjectByType<GeminiLiveAssistant>();
            if (gemini != null && talk == null)
            {
                // Same bindings as Gemini's push-to-talk: shows when the participant spoke.
                talk = new InputAction("Study Talk", InputActionType.Button);
                if (!string.IsNullOrEmpty(gemini.pushToTalkBinding)) talk.AddBinding(gemini.pushToTalkBinding);
                if (!string.IsNullOrEmpty(gemini.pushToTalkKey)) talk.AddBinding(gemini.pushToTalkKey);
                talk.Enable();
            }
        }
    }

    // ---- CSV -----------------------------------------------------------------

    private void AddButton(string name, string binding)
    {
        var action = new InputAction(name, InputActionType.Button, binding);
        buttons.Add(action);
    }

    private void ReadLaunchArguments()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "-participant", StringComparison.OrdinalIgnoreCase))
            {
                participant = args[i + 1];
                return;
            }
        }
    }

    /// <summary>Keeps only letters, digits, '_' and '-' so the code is safe in a file name.</summary>
    private static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "P00";
        var sb = new StringBuilder();
        foreach (char c in value.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "P00";
    }

    private void StartCsv()
    {
        startTime = Time.unscaledTime;
        lastFlush = startTime;
        string name = $"study_{participant}_{StudyCondition.Current}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv)}.csv";
        csvPath = Path.Combine(Application.persistentDataPath, name);
        try
        {
            File.WriteAllText(csvPath, "time_s,event,detail,view,period,x,y,z,yaw\n");
            Debug.Log($"StudyLogger: logging {participant} to {csvPath}");
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"StudyLogger: cannot write {csvPath} ({e.Message}); no log.");
            csvPath = null;
        }
    }

    private void Log(string evt, string detail) => Log(evt, detail, null, 0f);

    private void Log(string evt, string detail, Vector3? position, float yaw)
    {
        if (csvPath == null) return;
        string view = map != null ? (map.IsDome ? "dome" : map.IsMap ? "map" : "3d") : "3d";
        pending.Append((Time.unscaledTime - startTime).ToString("F2", Inv)).Append(',')
               .Append(evt).Append(',')
               .Append(Csv(detail)).Append(',')
               .Append(view).Append(',')
               .Append(Csv(PeriodText())).Append(',');
        if (position.HasValue)
        {
            Vector3 p = position.Value;
            pending.Append(p.x.ToString("F2", Inv)).Append(',')
                   .Append(p.y.ToString("F2", Inv)).Append(',')
                   .Append(p.z.ToString("F2", Inv)).Append(',')
                   .Append(yaw.ToString("F0", Inv));
        }
        else
        {
            pending.Append(",,,");
        }
        pending.Append('\n');
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") + "\"";
    }

    private void Flush()
    {
        lastFlush = Time.unscaledTime;
        if (csvPath == null || pending.Length == 0) return;
        try
        {
            File.AppendAllText(csvPath, pending.ToString());
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"StudyLogger: cannot write {csvPath} ({e.Message}).");
        }
        pending.Clear();
    }
}
