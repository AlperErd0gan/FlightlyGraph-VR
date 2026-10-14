using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Real-time spoken conversation with the assistant (TASKS B5) through the Gemini Live API:
/// hold Y on the left controller (N in the Editor), talk, release; the model answers with
/// its own voice, and can be interrupted by pressing Y again. Straight from Unity over one
/// WebSocket, no server in between.
/// The model drives the app through tools: app_command runs VoiceAssistant's commands
/// ("show istanbul", "filter cargo", ...), and get_* tools read figures from the loaded
/// graph, so answers come from the data instead of the model's memory.
/// While connected it takes over push to talk and selection narration from VoiceAssistant /
/// NodeNarrator (Wit.ai); when Gemini is unreachable or has no key, those work as before.
/// API key: environment variable GEMINI_API_KEY, else a text file named keyFileName in
/// Application.persistentDataPath (outside the project and the build; the path is logged).
/// The key stays in memory while the app runs: fine for development and demos; anything
/// shipped to other people needs ephemeral tokens from a small server.
/// </summary>
public class GeminiLiveAssistant : MonoBehaviour
{
    // Prebuilt voices; the first ones are the calm / informative candidates.
    public enum GeminiVoice
    {
        Charon, Iapetus, Rasalgethi, Sadaltager, Orus, Algenib, Alnilam, Schedar, Gacrux, Puck, Fenrir, Enceladus,
        Umbriel, Algieba, Achird, Zubenelgenubi, Sadachbia, Kore, Zephyr, Leda, Aoede, Callirrhoe, Autonoe, Despina,
        Erinome, Laomedeia, Achernar, Pulcherrima, Vindemiatrix, Sulafat
    }

    [Header("Scene (found automatically if unset)")]
    public GraphLoader graph;
    public GraphSelector selector;
    public VoiceAssistant commands;
    public NodeNarrator narrator;
    public GuidedTour tour;

    [Header("Gemini")]
    [Tooltip("Live API model id (ai.google.dev/gemini-api/docs/models).")]
    public string model = "gemini-3.8-live";
    [Tooltip("Voice of the answers. Changing it in Play mode reconnects.")]
    public GeminiVoice voice = GeminiVoice.Charon;
    public string assistantName = "Rebecca";
    [Tooltip("How the assistant speaks; the app and dataset description and the tool rules are added automatically.")]
    [TextArea(3, 8)]
    public string persona =
        "Speak like a calm, refined British AI butler with a dry wit, and address the user as \"sir\". " +
        "Keep every answer short, one to three sentences: it is spoken aloud in VR.";
    [Tooltip("Only carry out commands: the model gets no data tools, gives no facts or figures (neither from the data nor from its " +
             "own knowledge) and does not describe selections. Off = it also answers questions from the loaded data.")]
    public bool commandsOnly = true;
    [Tooltip("Understand and answer in English only (short phrases are otherwise sometimes taken for another language).")]
    public bool englishOnly = true;
    [Tooltip("File in Application.persistentDataPath holding only the API key (used when GEMINI_API_KEY is not set).")]
    public string keyFileName = "gemini_key.txt";
    [Tooltip("Allow sending figures from EUROCONTROL data (*_ectrl files) to Google. Only enable when the data licence permits it.")]
    public bool allowLicensedData = false;
    public bool connectOnStart = true;
    [Tooltip("When the user selects an airport or route by hand, the assistant describes it (instead of the Wit narrator).")]
    public bool describeSelections = true;

    [Header("Push to talk")]
    public string pushToTalkBinding = "<XRController>{LeftHand}/{SecondaryButton}";
    public string pushToTalkKey = "<Keyboard>/n";
    [Tooltip("Save what was sent to Gemini in the last press as a WAV file in Application.persistentDataPath (the path is logged), to listen to it.")]
    public bool saveLastInput = false;
    [Tooltip("Audio from just before the press that is sent too, so the first syllable is not lost (s).")]
    public float preRollSeconds = 0.2f;

    [Header("Voice effect")]
    [Tooltip("Light chorus + short reverb + high-pass: an AI-in-the-helmet sound.")]
    public bool helmetEffect = true;
    [Range(0f, 1f)] public float volume = 1f;

    /// <summary>Connected and set up: push to talk goes to Gemini.</summary>
    public bool IsReady => ready;
    /// <summary>The assistant's voice is playing.</summary>
    public bool IsSpeaking { get { lock (audioLock) return audioCount > 0; } }

    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const int InputRate = 16000;
    private const int OutputRate = 24000;

    // Connection (one object per WebSocket, so a reconnect never mixes queues).
    private sealed class Connection
    {
        public ClientWebSocket socket;
        public CancellationTokenSource cts;
        public readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
        public readonly SemaphoreSlim signal = new SemaphoreSlim(0);
    }

    private Connection connection;
    private volatile bool ready;
    private bool connecting;
    private bool disabled;
    private string apiKey;
    private int failures;
    private float reconnectAt = -1f;
    private bool reconnectAfterTurn;
    private float setupDeadline = -1f;
    private GeminiVoice connectedVoice;
    private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();

    // Audio out: ring buffer filled by the receive thread, read by the audio clip's callback.
    private readonly object audioLock = new object();
    private readonly float[] audioRing = new float[OutputRate * 90];
    private int audioRead, audioCount;
    private AudioSource audioSource;

    // Audio in.
    private InputAction talkAction;
    private string micDevice;
    private AudioClip micClip;
    private int micReadPos;
    private bool talking;
    private float[] micBuffer = new float[0];
    private readonly MemoryStream turnAudio = new MemoryStream();
    private float pressTime;
    private float turnPeak;
    private int watchPosition = -1;
    private float watchTime;

    // Captions and narration.
    private readonly StringBuilder heardText = new StringBuilder();
    private readonly StringBuilder saidText = new StringBuilder();
    private float suppressSelectionUntil;
    // A command result that stays above the transcript for a while (the "help" list).
    private string stickyCaption;
    private float stickyUntil;

    // Ranking cache for get_airport.
    private Dictionary<GraphNode, int> ranks;

    private void Awake()
    {
        talkAction = new InputAction("Gemini Push To Talk", InputActionType.Button);
        if (!string.IsNullOrEmpty(pushToTalkBinding)) talkAction.AddBinding(pushToTalkBinding);
        if (!string.IsNullOrEmpty(pushToTalkKey)) talkAction.AddBinding(pushToTalkKey);
        BuildAudioOutput();
    }

    private void OnEnable() => talkAction.Enable();

    private void OnDisable() => talkAction.Disable();

    private void Start()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (commands == null) commands = FindFirstObjectByType<VoiceAssistant>();
        if (narrator == null) narrator = FindFirstObjectByType<NodeNarrator>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (selector != null)
        {
            selector.NodeSelected += OnNodeSelected;
            selector.EdgeSelected += OnEdgeSelected;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
        }
#endif
        apiKey = ReadApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            disabled = true;
            Debug.LogWarning($"GeminiLiveAssistant: no API key. Put it in GEMINI_API_KEY or in " +
                             $"{Path.Combine(Application.persistentDataPath, keyFileName)}; using the Wit commands meanwhile.");
            Caption("Gemini: no API key found (path in the Console). Using the built-in commands.", 10f);
            return;
        }
        if (connectOnStart) reconnectAt = 0f; // once the graph has loaded
    }

    private void OnDestroy()
    {
        if (selector != null)
        {
            selector.NodeSelected -= OnNodeSelected;
            selector.EdgeSelected -= OnEdgeSelected;
        }
        talkAction.Dispose();
        Disconnect();
        StopMic();
    }

    private void OnValidate()
    {
        // A new voice only applies to a new session.
        if (Application.isPlaying && ready && voice != connectedVoice) reconnectAfterTurn = true;
    }

    private void Update()
    {
        while (mainThread.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception ex) { Debug.LogError($"GeminiLiveAssistant: {ex}"); }
        }

        if (commands != null) commands.Suspended = ready;
        // Gemini describes hand-made selections itself only when it may give information; otherwise the Wit narrator keeps
        // reading them (commands Gemini runs are kept silent in RunTool).
        if (narrator != null && ready && describeSelections && !commandsOnly && (tour == null || !tour.IsRunning)) narrator.AutoNarration = false;
        if (audioSource != null) audioSource.volume = volume;

        if (!disabled && !ready && !connecting && reconnectAt >= 0f && Time.time >= reconnectAt && graph != null && graph.IsLoaded)
        {
            reconnectAt = -1f;
            Connect();
        }
        if (reconnectAfterTurn && ready && !talking && !IsSpeaking)
        {
            reconnectAfterTurn = false;
            Disconnect();
            reconnectAt = 0f;
        }

        if (setupDeadline >= 0f && !ready && Time.time > setupDeadline)
        {
            setupDeadline = -1f;
            Debug.LogWarning($"GeminiLiveAssistant: no setupComplete from Gemini after 10 s (model '{model}'); still waiting.");
            Caption("Gemini is not answering the setup (see the Console). Using the built-in commands.", 8f);
        }

        if (!ready) return;
        if (!talking) WatchMic();
        if (talkAction.WasPressedThisFrame()) StartTalking();
        if (talking) SendMicAudio(flush: false);
        if (talking && talkAction.WasReleasedThisFrame()) StopTalking();
    }

    // ---- Connection ---------------------------------------------------------------

    private string ReadApiKey()
    {
        string key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
        string path = Path.Combine(Application.persistentDataPath, keyFileName);
        Debug.Log($"GeminiLiveAssistant: API key file: {path}");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex)
        {
            Debug.LogWarning($"GeminiLiveAssistant: could not read {path}: {ex.Message}");
            return null;
        }
    }

    private bool UsesLicensedData =>
        graph != null && ((graph.nodesFileName ?? "").Contains("ectrl") || (graph.edgesFileName ?? "").Contains("ectrl"));

    [ContextMenu("Reconnect")]
    public void Reconnect()
    {
        Disconnect();
        failures = 0;
        reconnectAt = 0f;
    }

    private void Connect()
    {
        if (UsesLicensedData && !allowLicensedData)
        {
            disabled = true;
            Caption("Gemini is off for EUROCONTROL data (licence). Using the built-in commands.", 6f);
            Debug.LogWarning("GeminiLiveAssistant: this scene uses EUROCONTROL data; enable Allow Licensed Data only if the licence permits sending it to Google.");
            return;
        }

        connecting = true;
        connectedVoice = voice;
        string setup = BuildSetupMessage();
        var conn = new Connection { socket = new ClientWebSocket(), cts = new CancellationTokenSource() };
        connection = conn;
        string uri = $"{Endpoint}?key={Uri.EscapeDataString(apiKey)}";
        Task.Run(async () =>
        {
            try
            {
                await conn.socket.ConnectAsync(new Uri(uri), conn.cts.Token).ConfigureAwait(false);
                Send(conn, setup);
                _ = Task.Run(() => SendLoop(conn));
                await ReceiveLoop(conn).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!conn.cts.IsCancellationRequested) mainThread.Enqueue(() => OnClosed(conn, ex.Message));
            }
        });
        setupDeadline = Time.time + 10f;
        Debug.Log($"GeminiLiveAssistant: connecting ({model}, voice {voice})");
        Caption("Connecting to Gemini...", 10f);
    }

    private void Disconnect()
    {
        Connection conn = connection;
        connection = null;
        ready = false;
        connecting = false;
        talking = false;
        ClearAudio();
        if (conn == null) return;
        try
        {
            conn.cts.Cancel();
            conn.socket.Abort();
            conn.socket.Dispose();
        }
        catch (Exception) { /* already closed */ }
    }

    private void OnReady(Connection conn)
    {
        if (conn != connection) return;
        connecting = false;
        ready = true;
        failures = 0;
        setupDeadline = -1f;
        StartMic(); // kept open while connected: opening it per press stalls a frame and loses the first second
        Debug.Log("GeminiLiveAssistant: ready");
        Caption($"{assistantName} is listening: hold <b>Y</b> and talk.", 5f);
    }

    private void OnClosed(Connection conn, string reason)
    {
        if (conn != connection) return; // an old connection
        bool wasReady = ready;
        Disconnect();
        StopMic();
        if (narrator != null) narrator.AutoNarration = true;
        failures++;
        setupDeadline = -1f;
        Debug.LogWarning($"GeminiLiveAssistant: connection closed: {reason}");
        if (!wasReady) Caption($"Gemini connection failed: {Escape(Truncate(reason, 120))}", 8f);
        // Sessions end after a while (goAway / time limit): reconnect quietly; stop after repeated failures.
        if (failures <= 3)
        {
            reconnectAt = Time.time + (wasReady ? 0.5f : 3f * failures);
        }
        else
        {
            Caption($"Gemini unavailable ({reason}). Using the built-in commands.", 8f);
        }
    }

    private static void Send(Connection conn, string json)
    {
        conn.outgoing.Enqueue(json);
        conn.signal.Release();
    }

    private void Send(string json)
    {
        Connection conn = connection;
        if (conn != null) Send(conn, json);
    }

    private static async Task SendLoop(Connection conn)
    {
        try
        {
            while (!conn.cts.IsCancellationRequested)
            {
                await conn.signal.WaitAsync(conn.cts.Token).ConfigureAwait(false);
                if (!conn.outgoing.TryDequeue(out string json)) continue;
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await conn.socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, conn.cts.Token)
                                 .ConfigureAwait(false);
            }
        }
        catch (Exception) { /* closed: ReceiveLoop reports it */ }
    }

    private async Task ReceiveLoop(Connection conn)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        string reason = "closed";
        while (!conn.cts.IsCancellationRequested && conn.socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await conn.socket.ReceiveAsync(new ArraySegment<byte>(buffer), conn.cts.Token)
                                                             .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                reason = $"{conn.socket.CloseStatus} {conn.socket.CloseStatusDescription}".Trim();
                break;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try { HandleServerMessage(conn, json); }
            catch (Exception ex) { Debug.LogWarning($"GeminiLiveAssistant: bad message ({ex.Message}): {Truncate(json, 300)}"); }
        }
        if (!conn.cts.IsCancellationRequested) mainThread.Enqueue(() => OnClosed(conn, reason));
    }

    /// <summary>Runs on the receive thread: audio goes straight to the buffer, the rest to the main thread.</summary>
    private void HandleServerMessage(Connection conn, string json)
    {
        JObject msg = JObject.Parse(json);
        if (msg["setupComplete"] != null) mainThread.Enqueue(() => OnReady(conn));

        if (msg["serverContent"] is JObject content)
        {
            if (content.Value<bool?>("interrupted") == true) ClearAudio();
            if (content["modelTurn"]?["parts"] is JArray parts)
            {
                foreach (JToken part in parts)
                {
                    JToken inline = part["inlineData"];
                    string mime = inline?.Value<string>("mimeType") ?? "";
                    string data = inline?.Value<string>("data");
                    if (data != null && mime.StartsWith("audio/pcm", StringComparison.Ordinal)) AddAudio(Convert.FromBase64String(data));
                }
            }
            string heard = content["inputTranscription"]?.Value<string>("text");
            string said = content["outputTranscription"]?.Value<string>("text");
            bool turnComplete = content.Value<bool?>("turnComplete") == true;
            if (heard != null || said != null || turnComplete) mainThread.Enqueue(() => OnTranscript(heard, said, turnComplete));
        }

        if (msg["toolCall"] is JObject toolCall) mainThread.Enqueue(() => HandleToolCall(toolCall));
        if (msg["goAway"] != null) mainThread.Enqueue(() => reconnectAfterTurn = true);
    }

    // ---- Setup ----------------------------------------------------------------------

    private string BuildSetupMessage()
    {
        var setup = new JObject
        {
            ["model"] = model.StartsWith("models/", StringComparison.Ordinal) ? model : "models/" + model,
            ["generationConfig"] = new JObject
            {
                ["responseModalities"] = new JArray("AUDIO"),
                ["speechConfig"] = new JObject
                {
                    ["voiceConfig"] = new JObject { ["prebuiltVoiceConfig"] = new JObject { ["voiceName"] = voice.ToString() } }
                }
            },
            ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = SystemPrompt() }) },
            ["tools"] = new JArray(new JObject { ["functionDeclarations"] = ToolDeclarations(commandsOnly) }),
            // Push to talk: the button marks the start and end of each turn.
            ["realtimeInputConfig"] = new JObject { ["automaticActivityDetection"] = new JObject { ["disabled"] = true } },
            ["inputAudioTranscription"] = new JObject(),
            ["outputAudioTranscription"] = new JObject()
        };
        return new JObject { ["setup"] = setup }.ToString(Formatting.None);
    }

    private string SystemPrompt()
    {
        // Same text as the version that kept the model in English; commandsOnly only adds rules at the end.
        var sb = new StringBuilder();
        sb.Append($"You are {assistantName}, the voice of FlightlyVR, a virtual-reality app in which the user stands inside a 3D graph: ");
        sb.Append("spheres are airports (bigger = busier), arcs are flight routes. ");
        string style = persona ?? "";
        if (englishOnly)
        {
            // Older scenes saved a persona that asked for the user's language.
            style = style.Replace("Reply in the language the user speaks.", "").Trim();
            sb.Append(style).Append(' ');
            sb.Append("The user always speaks English: interpret everything they say as English, even short or unclear phrases ");
            sb.Append("that sound like another language, and always reply in English only. ");
        }
        else
        {
            sb.Append(style).Append(' ');
        }
        sb.Append("Never invent figures: get every number from the tools, and say so when a tool has no answer. ");
        sb.Append("When the user wants to see, show, find, filter, compare or go somewhere, call app_command (several times if needed), ");
        sb.Append("then say in a few words what you did. Speak numbers in a natural, rounded way. ");
        sb.Append("If the user says \"Jarvis, I'm home\", answer exactly: \"Hello, Mr. Stark.\" ");
        sb.Append("Dataset: ").Append(DatasetSummary());
        if (commandsOnly)
        {
            sb.Append(" Your only job is to operate the app: for every request call app_command, then confirm in a few words ");
            sb.Append("what you did, for example \"Showing Istanbul, sir.\". Never give extra information: no facts, figures, ");
            sb.Append("history or explanations, neither from your own knowledge nor from the tool results. If a request is not ");
            sb.Append("something app_command can do, say in one short sentence that you can only control the app and that ");
            sb.Append("\"help\" lists the commands. When the user says \"help\" or asks what they can say, call app_command(\"help\") ");
            sb.Append("and answer only \"Here are the commands, sir.\".");
        }
        return sb.ToString();
    }

    private string DatasetSummary()
    {
        if (graph == null || !graph.IsLoaded) return "not loaded.";
        if (graph.HasTimeAxis)
        {
            return $"EUROCONTROL flights in Europe, {graph.PeriodName(0)} to {graph.PeriodName(graph.PeriodCount - 1)} " +
                   $"({graph.PeriodCount} months, monthly figures), the {graph.LoadedNodeCount} busiest airports and " +
                   $"{graph.LoadedEdgeCount} routes; an airport's value and a route's weight are numbers of flights.";
        }
        return $"OpenFlights world routes (a 2014 snapshot, no dates), the {graph.LoadedNodeCount} best connected airports and " +
               $"{graph.LoadedEdgeCount} routes; an airport's value is its number of routes, a route's weight the number of " +
               "airlines flying it. There are no flight counts or months in this dataset.";
    }

    private static JArray ToolDeclarations(bool commandsOnly)
    {
        JObject appCommand = Tool("app_command",
                 "Changes what the user sees in the VR app. One short English command, one of: 'show <airport, city or code>', " +
                 "'help', 'route from <A> to <B>', 'clear', " + (commandsOnly ? "" : "'describe', ") + "'regional view', 'top routes', 'filter <market segment or country>', " +
                 "'reset filters', 'open timeline', 'play', 'pause', 'show <month> <year>', 'show <year>', 'all months', 'next month', " +
                 "'previous month', 'open dashboard', 'close dashboard', 'open insights', 'open filters', 'open controls', " +
                 "'open overview', 'group cities', 'ungroup cities', 'go to the centre', 'start tour', 'stop tour'. " +
                 "Returns what happened.",
                 Param("command", "STRING", "The command.", true));
        if (commandsOnly) return new JArray { appCommand };
        return new JArray
        {
            appCommand,
            Tool("get_dataset_info", "What the loaded dataset contains and what the user currently sees (month, filter, view, selection)."),
            Tool("get_airport",
                 "Figures for one airport: totals, rank, per-year and busiest / quietest months (when the data has months), " +
                 "delays, cargo share, market segments, busiest routes.",
                 Param("name", "STRING", "Airport name, city or code, e.g. 'Istanbul', 'FRA'.", true)),
            Tool("get_route",
                 "Figures for the route between two airports (flights, per year, distance, duration, delay), or the stops when there is no direct route.",
                 Param("from", "STRING", "First airport (name, city or code).", true),
                 Param("to", "STRING", "Second airport (name, city or code).", true)),
            Tool("get_top_airports",
                 "The busiest airports, overall or in one month.",
                 Param("count", "INTEGER", "How many (1-20).", false),
                 Param("month", "STRING", "Optional month 'yyyy-MM'.", false)),
            Tool("get_insights", "Facts found in the data: fastest / slowest recovery after COVID, biggest growth, cargo hubs and routes."),
            Tool("get_selection", "What the user has selected in the scene right now.")
        };
    }

    private static JObject Tool(string name, string description, params (string name, string type, string description, bool required)[] parameters)
    {
        var tool = new JObject { ["name"] = name, ["description"] = description };
        if (parameters.Length == 0) return tool;
        var properties = new JObject();
        var required = new JArray();
        foreach (var p in parameters)
        {
            properties[p.name] = new JObject { ["type"] = p.type, ["description"] = p.description };
            if (p.required) required.Add(p.name);
        }
        var schema = new JObject { ["type"] = "OBJECT", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        tool["parameters"] = schema;
        return tool;
    }

    private static (string, string, string, bool) Param(string name, string type, string description, bool required) =>
        (name, type, description, required);

    // ---- Talking -----------------------------------------------------------------------

    private void StartTalking()
    {
        if (micClip == null || !Microphone.IsRecording(micDevice))
        {
            StopMic();
            StartMic();
        }
        if (micClip == null)
        {
            Caption("No microphone.", 3f);
            return;
        }
        talking = true;
        ClearAudio(); // barge in: stop the current answer at once
        if (narrator != null) narrator.Stop();
        heardText.Length = 0;
        saidText.Length = 0;
        turnAudio.SetLength(0);
        turnPeak = 0f;
        pressTime = Time.realtimeSinceStartup;
        int length = micClip.samples;
        int position = Microphone.GetPosition(micDevice);
        micReadPos = ((position - Mathf.RoundToInt(preRollSeconds * micClip.frequency)) % length + length) % length;
        Send("{\"realtimeInput\":{\"activityStart\":{}}}");
        Caption("Listening... release <b>Y</b> when done.", 30f);
        PlayUISound(UISounds.Open);
    }

    private void StopTalking()
    {
        SendMicAudio(flush: true);
        talking = false;
        int micRate = micClip != null ? micClip.frequency : 0;
        Send("{\"realtimeInput\":{\"activityEnd\":{}}}");
        float sentSeconds = turnAudio.Length / 2f / InputRate;
        float heldSeconds = Time.realtimeSinceStartup - pressTime;
        string saved = "";
        if (saveLastInput)
        {
            string path = Path.Combine(Application.persistentDataPath, "gemini_last_input.wav");
            try
            {
                WriteWav(path, turnAudio.ToArray(), InputRate);
                saved = $"; saved to {path}";
            }
            catch (Exception ex) { saved = $"; could not save: {ex.Message}"; }
        }
        float peakDb = turnPeak > 1e-6f ? 20f * Mathf.Log10(turnPeak) : -120f;
        Debug.Log($"GeminiLiveAssistant: sent {sentSeconds:F2} s of audio for a {heldSeconds:F2} s press, peak {peakDb:F0} dB " +
                  $"(mic '{micDevice}' {micRate} Hz){saved}");
        if (peakDb < -50f)
        {
            Debug.LogWarning($"GeminiLiveAssistant: the microphone delivered (almost) silence; reopening it. Devices: [{string.Join(", ", Microphone.devices)}]");
            Caption("The microphone sent silence; it was reset, please try again.", 6f);
            StopMic();
            StartMic();
            return;
        }
        Caption(heardText.Length > 0 ? $"<color=#9AA6BA>\"{Escape(heardText.ToString())}\"</color>" : "<color=#9AA6BA>...</color>", 8f);
    }

    private void StartMic()
    {
        if (micClip != null || Microphone.devices.Length == 0) return;
        micDevice = PickMicrophone();
        // The device's own rate (the Quest headset microphone only offers 48 kHz); SendMicAudio converts to 16 kHz.
        Microphone.GetDeviceCaps(micDevice, out int minRate, out int maxRate);
        int rate = maxRate > 0 ? Mathf.Max(InputRate, Mathf.Min(maxRate, 48000)) : InputRate;
        micClip = Microphone.Start(micDevice, true, 4, rate);
        watchPosition = -1;
        Debug.Log($"GeminiLiveAssistant: microphone '{micDevice}' at {(micClip != null ? micClip.frequency : 0)} Hz " +
                  $"(device supports {minRate}-{maxRate} Hz; 0-0 = any); all: [{string.Join(", ", Microphone.devices)}]");
    }

    private void StopMic()
    {
        if (micClip == null) return;
        Microphone.End(micDevice);
        micClip = null;
    }

    /// <summary>
    /// The recording must keep moving while it is open; when another user of the device (the Voice SDK) takes it over,
    /// ours stops without an error. Checked twice a second between presses; a stalled recording is reopened.
    /// </summary>
    private void WatchMic()
    {
        if (micClip == null || Time.time < watchTime) return;
        watchTime = Time.time + 0.5f;
        int position = Microphone.GetPosition(micDevice);
        if (position == watchPosition || !Microphone.IsRecording(micDevice))
        {
            Debug.LogWarning("GeminiLiveAssistant: the microphone stopped recording; reopening it.");
            StopMic();
            StartMic();
            return;
        }
        watchPosition = position;
    }

    private static string PickMicrophone()
    {
        foreach (string name in Microphone.devices)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("oculus") || lower.Contains("quest") || lower.Contains("headset")) return name;
        }
        return Microphone.devices[0];
    }

    /// <summary>Sends the microphone samples recorded since the last call, in chunks of about 100 ms.</summary>
    private void SendMicAudio(bool flush)
    {
        if (micClip == null) return;
        int length = micClip.samples;
        int position = Microphone.GetPosition(micDevice);
        int available = (position - micReadPos + length) % length;
        int chunk = micClip.frequency / 10;
        if (available == 0 || (!flush && available < chunk)) return;

        // Whole output samples only; the rest stays for the next call.
        float step = micClip.frequency / (float)InputRate;
        int outCount = Mathf.FloorToInt(available / step);
        if (outCount == 0) return;
        available = Mathf.Min(available, Mathf.CeilToInt(outCount * step));
        if (micBuffer.Length < available) micBuffer = new float[available];
        int first = Mathf.Min(available, length - micReadPos);
        var part = new float[first];
        micClip.GetData(part, micReadPos);
        Array.Copy(part, 0, micBuffer, 0, first);
        if (available > first)
        {
            var rest = new float[available - first];
            micClip.GetData(rest, 0);
            Array.Copy(rest, 0, micBuffer, first, rest.Length);
        }
        micReadPos = (micReadPos + available) % length;

        // To 16 kHz 16-bit little-endian PCM, averaging the input samples of each output sample (a simple low-pass).
        var bytes = new byte[outCount * 2];
        for (int i = 0; i < outCount; i++)
        {
            int from = Mathf.FloorToInt(i * step);
            int to = Mathf.Min(available, Mathf.Max(from + 1, Mathf.FloorToInt((i + 1) * step)));
            float sum = 0f;
            for (int k = from; k < to; k++) sum += micBuffer[k];
            float sample = Mathf.Clamp(sum / (to - from), -1f, 1f);
            turnPeak = Mathf.Max(turnPeak, Mathf.Abs(sample));
            short value = (short)Mathf.RoundToInt(sample * 32767f);
            bytes[2 * i] = (byte)(value & 0xff);
            bytes[2 * i + 1] = (byte)((value >> 8) & 0xff);
        }
        turnAudio.Write(bytes, 0, bytes.Length);
        Send("{\"realtimeInput\":{\"audio\":{\"mimeType\":\"audio/pcm;rate=16000\",\"data\":\"" + Convert.ToBase64String(bytes) + "\"}}}");
    }

    // ---- Audio out ---------------------------------------------------------------------

    private void BuildAudioOutput()
    {
        var voiceObject = new GameObject("Gemini Voice");
        voiceObject.transform.SetParent(transform, false);
        audioSource = voiceObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.loop = true;
        audioSource.playOnAwake = false;
        // A streamed clip whose callback reads the ring buffer (silence when it is empty).
        audioSource.clip = AudioClip.Create("Gemini Stream", OutputRate, 1, OutputRate, true, ReadAudio);
        if (helmetEffect)
        {
            AudioHighPassFilter highPass = voiceObject.AddComponent<AudioHighPassFilter>();
            highPass.cutoffFrequency = 140f;
            AudioChorusFilter chorus = voiceObject.AddComponent<AudioChorusFilter>();
            chorus.dryMix = 0.85f;
            chorus.wetMix1 = 0.22f;
            chorus.wetMix2 = 0.18f;
            chorus.wetMix3 = 0f;
            chorus.delay = 14f;
            chorus.rate = 0.4f;
            chorus.depth = 0.04f;
            AudioReverbFilter reverb = voiceObject.AddComponent<AudioReverbFilter>();
            reverb.reverbPreset = AudioReverbPreset.User;
            reverb.dryLevel = 0f;
            reverb.room = -1800f;
            reverb.roomHF = -900f;
            reverb.decayTime = 0.45f;
            reverb.reflectionsLevel = -1400f;
            reverb.reverbLevel = -700f;
        }
        audioSource.Play();
    }

    private void ReadAudio(float[] data)
    {
        lock (audioLock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (audioCount == 0)
                {
                    data[i] = 0f;
                    continue;
                }
                data[i] = audioRing[audioRead];
                audioRead = (audioRead + 1) % audioRing.Length;
                audioCount--;
            }
        }
    }

    private void AddAudio(byte[] pcm)
    {
        lock (audioLock)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                if (audioCount == audioRing.Length) // full: drop the oldest
                {
                    audioRead = (audioRead + 1) % audioRing.Length;
                    audioCount--;
                }
                short value = (short)(pcm[i] | (pcm[i + 1] << 8));
                audioRing[(audioRead + audioCount) % audioRing.Length] = value / 32768f;
                audioCount++;
            }
        }
    }

    private void ClearAudio()
    {
        lock (audioLock)
        {
            audioRead = 0;
            audioCount = 0;
        }
    }

    // ---- Transcripts and selections ------------------------------------------------------

    private void OnTranscript(string heard, string said, bool turnComplete)
    {
        if (heard != null) heardText.Append(heard);
        if (said != null) saidText.Append(said);
        var sb = new StringBuilder();
        if (stickyCaption != null && Time.time < stickyUntil) sb.Append(stickyCaption).Append('\n');
        if (heardText.Length > 0) sb.Append("<color=#9AA6BA>\"").Append(Escape(heardText.ToString().Trim())).Append("\"</color>\n");
        if (saidText.Length > 0) sb.Append(Escape(saidText.ToString().Trim()));
        if (sb.Length > 0) Caption(sb.ToString(), turnComplete ? 6f : 30f);
        if (turnComplete)
        {
            if (heardText.Length > 0 || saidText.Length > 0) Debug.Log($"GeminiLiveAssistant: \"{heardText}\" -> \"{saidText}\"");
            heardText.Length = 0;
            saidText.Length = 0;
        }
    }

    private void OnNodeSelected(GraphNode node)
    {
        if (!ShouldDescribeSelection() || node == null) return;
        SendText($"(The user just selected the airport {node.label} in the scene. Tell them about it in one or two sentences; use get_airport.)");
    }

    private void OnEdgeSelected(GraphEdge edge)
    {
        if (!ShouldDescribeSelection() || edge == null || graph == null) return;
        string a = graph.Nodes.TryGetValue(edge.sourceId, out GraphNode na) ? na.label : edge.sourceId;
        string b = graph.Nodes.TryGetValue(edge.targetId, out GraphNode nb) ? nb.label : edge.targetId;
        SendText($"(The user just selected the route between {a} and {b}. Tell them about it in one or two sentences; use get_route.)");
    }

    private bool ShouldDescribeSelection() =>
        ready && describeSelections && !commandsOnly && !talking && Time.time >= suppressSelectionUntil && (tour == null || !tour.IsRunning);

    private void SendText(string text)
    {
        var message = new JObject
        {
            ["clientContent"] = new JObject
            {
                ["turns"] = new JArray(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = text }) }),
                ["turnComplete"] = true
            }
        };
        ClearAudio();
        Send(message.ToString(Formatting.None));
    }

    // ---- Tools -------------------------------------------------------------------------------

    private void HandleToolCall(JObject toolCall)
    {
        var responses = new JArray();
        if (toolCall["functionCalls"] is JArray calls)
        {
            foreach (JToken call in calls)
            {
                string name = call.Value<string>("name");
                JObject args = call["args"] as JObject ?? new JObject();
                JObject result;
                try { result = RunTool(name, args); }
                catch (Exception ex)
                {
                    Debug.LogError($"GeminiLiveAssistant: tool {name} failed: {ex}");
                    result = new JObject { ["error"] = "The tool failed." };
                }
                Debug.Log($"GeminiLiveAssistant: tool {name}({args.ToString(Formatting.None)}) -> {Truncate(result.ToString(Formatting.None), 300)}");
                responses.Add(new JObject { ["id"] = call.Value<string>("id"), ["name"] = name, ["response"] = result });
            }
        }
        Send(new JObject { ["toolResponse"] = new JObject { ["functionResponses"] = responses } }.ToString(Formatting.None));
    }

    private JObject RunTool(string name, JObject args)
    {
        switch (name)
        {
            case "app_command":
            {
                if (commands == null) return new JObject { ["error"] = "Commands are not available in this scene." };
                string command = args.Value<string>("command") ?? "";
                if (commandsOnly && VoiceAssistant.Normalize(command).StartsWith("describe", StringComparison.Ordinal))
                {
                    return new JObject { ["done"] = false, ["result"] = "Describing is turned off: only commands are carried out." };
                }
                suppressSelectionUntil = Time.time + 2f; // the selection it makes is described by this answer
                // Gemini answers itself: the Wit narrator must not read out what the command selects.
                bool narrating = narrator != null && narrator.AutoNarration;
                if (narrator != null) narrator.AutoNarration = false;
                bool ok;
                string reply;
                try { ok = commands.RunCommand(command, out reply); }
                finally { if (narrator != null) narrator.AutoNarration = narrating; }
                if (ok && VoiceAssistant.Normalize(command) == "help")
                {
                    stickyCaption = reply;
                    stickyUntil = Time.time + 15f;
                    Caption(reply, 15f);
                }
                return new JObject { ["done"] = ok, ["result"] = reply };
            }
            case "get_dataset_info" when commandsOnly:
            case "get_airport" when commandsOnly:
            case "get_route" when commandsOnly:
            case "get_top_airports" when commandsOnly:
            case "get_insights" when commandsOnly:
            case "get_selection" when commandsOnly:
                return new JObject { ["error"] = "Data tools are turned off: only commands are carried out." };
            case "get_dataset_info": return DatasetInfo();
            case "get_airport":
            {
                GraphNode node = FindAirport(args.Value<string>("name"));
                return node != null ? AirportInfo(node, full: true) : NotFound(args.Value<string>("name"));
            }
            case "get_route": return RouteInfo(args.Value<string>("from"), args.Value<string>("to"));
            case "get_top_airports": return TopAirports(args.Value<int?>("count") ?? 5, args.Value<string>("month"));
            case "get_insights": return Insights();
            case "get_selection": return Selection();
            default: return new JObject { ["error"] = $"Unknown tool {name}." };
        }
    }

    private GraphNode FindAirport(string spoken) => commands != null && !string.IsNullOrEmpty(spoken) ? commands.FindAirport(spoken) : null;

    private static JObject NotFound(string spoken) => new JObject { ["error"] = $"No airport called '{spoken}' in this graph." };

    private string ValueUnit => graph.HasTimeAxis ? "flights" : "routes";

    private JObject DatasetInfo()
    {
        var info = new JObject
        {
            ["description"] = DatasetSummary(),
            ["airports"] = graph.LoadedNodeCount,
            ["routes"] = graph.LoadedEdgeCount,
            ["view"] = graph.RegionalView ? "regional clusters" : "top routes",
            ["shownMonth"] = graph.HasPeriod ? graph.PeriodName(graph.Period) : "all months",
        };
        if (graph.HasTimeAxis)
        {
            info["firstMonth"] = graph.PeriodName(0);
            info["lastMonth"] = graph.PeriodName(graph.PeriodCount - 1);
        }
        if (graph.HasFilter)
        {
            info["filter"] = new JObject
            {
                ["segment"] = graph.FilterSegment, ["country"] = graph.FilterCountry, ["minFlightsPerRoute"] = graph.FilterMinWeight
            };
        }
        var segments = new HashSet<string>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (e.data?.segments != null) segments.UnionWith(e.data.segments.Keys);
        }
        if (segments.Count > 0) info["marketSegments"] = new JArray(segments);
        info["selection"] = Selection();
        return info;
    }

    private JObject AirportInfo(GraphNode node, bool full)
    {
        NodeData d = node.data;
        var info = new JObject
        {
            ["name"] = node.label,
            ["code"] = node.ShortCode,
            ["icao"] = node.id,
            [graph.HasTimeAxis ? "totalFlights" : "routes"] = node.value,
            ["rank"] = Rank(node),
            ["ofAirports"] = graph.LoadedNodeCount
        };
        if (d == null) return info;
        if (!string.IsNullOrEmpty(d.city)) info["city"] = d.city;
        if (!string.IsNullOrEmpty(d.country)) info["country"] = d.country;
        if (!full) return info;

        if (d.departures > 0 || d.arrivals > 0)
        {
            info["departures"] = d.departures;
            info["arrivals"] = d.arrivals;
        }
        if (d.avgDepDelayMin.HasValue) info["avgDepartureDelayMin"] = Math.Round(d.avgDepDelayMin.Value, 1);
        if (d.avgArrDelayMin.HasValue) info["avgArrivalDelayMin"] = Math.Round(d.avgArrDelayMin.Value, 1);
        if (d.monthly != null && graph.HasTimeAxis) AddMonthly(info, d.monthly);
        if (d.cargoShare > 0f) info["cargoShare"] = Math.Round(d.cargoShare, 3);
        if (d.segments != null) info["segments"] = TopEntries(d.segments, 5);
        if (!string.IsNullOrEmpty(d.topOperator)) info["topOperator"] = d.topOperator;
        if (!string.IsNullOrEmpty(d.topAcType)) info["topAircraftType"] = d.topAcType;

        var routes = new List<GraphEdge>(graph.EdgesOf(node.id));
        routes.Sort((a, b) => b.weight.CompareTo(a.weight));
        var busiest = new JArray();
        for (int i = 0; i < routes.Count && i < 5; i++)
        {
            string other = routes[i].sourceId == node.id ? routes[i].targetId : routes[i].sourceId;
            string label = graph.Nodes.TryGetValue(other, out GraphNode o) ? o.label : other;
            busiest.Add(new JObject { ["to"] = label, [graph.HasTimeAxis ? "flights" : "airlines"] = routes[i].weight });
        }
        info["busiestRoutes"] = busiest;
        info["routesInGraph"] = routes.Count;
        return info;
    }

    /// <summary>Per-year totals, busiest / quietest month and the shown month's figure.</summary>
    private void AddMonthly(JObject info, int[] monthly)
    {
        var years = new SortedDictionary<string, long>();
        int busiest = -1, quietest = -1;
        for (int i = 0; i < monthly.Length && i < graph.PeriodCount; i++)
        {
            string period = graph.PeriodName(i);
            string year = period.Substring(0, 4);
            years.TryGetValue(year, out long sum);
            years[year] = sum + monthly[i];
            if (busiest < 0 || monthly[i] > monthly[busiest]) busiest = i;
            if (quietest < 0 || monthly[i] < monthly[quietest]) quietest = i;
        }
        var perYear = new JObject();
        foreach (KeyValuePair<string, long> kv in years) perYear[kv.Key] = kv.Value;
        info["flightsPerYear"] = perYear;
        info["note"] = $"Data runs from {graph.PeriodName(0)} to {graph.PeriodName(graph.PeriodCount - 1)}; first and last years may be partial.";
        if (busiest >= 0) info["busiestMonth"] = new JObject { ["month"] = graph.PeriodName(busiest), ["flights"] = monthly[busiest] };
        if (quietest >= 0) info["quietestMonth"] = new JObject { ["month"] = graph.PeriodName(quietest), ["flights"] = monthly[quietest] };
        if (graph.HasPeriod && graph.Period < monthly.Length)
        {
            info["shownMonth"] = new JObject { ["month"] = graph.PeriodName(graph.Period), ["flights"] = monthly[graph.Period] };
        }
    }

    private JObject RouteInfo(string fromText, string toText)
    {
        GraphNode a = FindAirport(fromText);
        GraphNode b = FindAirport(toText);
        if (a == null) return NotFound(fromText);
        if (b == null) return NotFound(toText);
        var info = new JObject { ["from"] = a.label, ["to"] = b.label };
        foreach (GraphEdge e in graph.EdgesOf(a.id))
        {
            if (e.sourceId != b.id && e.targetId != b.id) continue;
            EdgeData d = e.data;
            info["direct"] = true;
            info[graph.HasTimeAxis ? "flights" : "airlines"] = e.weight;
            if (d == null) return info;
            if (d.forward > 0 || d.backward > 0)
            {
                info[$"flights {e.sourceId}->{e.targetId}"] = d.forward;
                info[$"flights {e.targetId}->{e.sourceId}"] = d.backward;
            }
            if (d.avgDistanceNm.HasValue) info["avgDistanceNm"] = Math.Round(d.avgDistanceNm.Value);
            if (d.avgDurationMin.HasValue) info["avgDurationMin"] = Math.Round(d.avgDurationMin.Value);
            if (d.avgDelayMin.HasValue) info["avgDelayMin"] = Math.Round(d.avgDelayMin.Value, 1);
            if (d.cargoShare > 0f) info["cargoShare"] = Math.Round(d.cargoShare, 3);
            if (!string.IsNullOrEmpty(d.topOperator)) info["topOperator"] = d.topOperator;
            if (d.monthly != null && graph.HasTimeAxis) AddMonthly(info, d.monthly);
            return info;
        }

        info["direct"] = false;
        ConnectionFinder finder = FindFirstObjectByType<ConnectionFinder>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (finder != null && finder.TryFindPath(a, b, nodes, edges))
        {
            var stops = new JArray();
            foreach (GraphNode n in nodes) stops.Add(n.label);
            info["fewestStopsRoute"] = stops;
        }
        else
        {
            info["fewestStopsRoute"] = "none in this graph";
        }
        return info;
    }

    private JObject TopAirports(int count, string month)
    {
        count = Mathf.Clamp(count, 1, 20);
        int period = -1;
        if (!string.IsNullOrEmpty(month) && graph.HasTimeAxis)
        {
            for (int i = 0; i < graph.PeriodCount; i++)
            {
                if (graph.PeriodName(i) == month) period = i;
            }
            if (period < 0) return new JObject { ["error"] = $"No month {month}; data runs {graph.PeriodName(0)} to {graph.PeriodName(graph.PeriodCount - 1)}." };
        }
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        int Value(GraphNode n) => period >= 0 && n.data?.monthly != null && period < n.data.monthly.Length ? n.data.monthly[period] : n.value;
        nodes.Sort((x, y) => Value(y).CompareTo(Value(x)));
        var list = new JArray();
        for (int i = 0; i < count && i < nodes.Count; i++)
        {
            JObject item = AirportInfo(nodes[i], full: false);
            item.Remove("rank");
            item.Remove("ofAirports");
            item["rank"] = i + 1;
            item[period >= 0 ? "flightsInMonth" : graph.HasTimeAxis ? "totalFlights" : "routes"] = Value(nodes[i]);
            list.Add(item);
        }
        return new JObject { ["month"] = period >= 0 ? month : "all months", ["airports"] = list };
    }

    private JObject Insights()
    {
        List<GraphInsight> insights = GraphInsights.Compute(graph, graph.Meta);
        var list = new JArray();
        foreach (GraphInsight insight in insights) list.Add(new JObject { ["category"] = insight.category, ["fact"] = insight.text });
        if (list.Count == 0) return new JObject { ["note"] = "No insights: this dataset has no months or market segments." };
        return new JObject { ["insights"] = list };
    }

    private JObject Selection()
    {
        if (selector == null) return new JObject { ["selected"] = "nothing" };
        if (selector.SelectedNode != null) return new JObject { ["selectedAirport"] = AirportInfo(selector.SelectedNode, full: false) };
        GraphEdge edge = selector.SelectedEdge;
        if (edge != null)
        {
            string a = graph.Nodes.TryGetValue(edge.sourceId, out GraphNode na) ? na.label : edge.sourceId;
            string b = graph.Nodes.TryGetValue(edge.targetId, out GraphNode nb) ? nb.label : edge.targetId;
            return new JObject { ["selectedRoute"] = new JObject { ["from"] = a, ["to"] = b, [ValueUnit == "flights" ? "flights" : "airlines"] = edge.weight } };
        }
        return new JObject { ["selected"] = "nothing" };
    }

    private int Rank(GraphNode node)
    {
        if (ranks == null || ranks.Count != graph.Nodes.Count)
        {
            var nodes = new List<GraphNode>(graph.Nodes.Values);
            nodes.Sort((a, b) => b.value.CompareTo(a.value));
            ranks = new Dictionary<GraphNode, int>();
            for (int i = 0; i < nodes.Count; i++) ranks[nodes[i]] = i + 1;
        }
        return ranks.TryGetValue(node, out int rank) ? rank : 0;
    }

    private static JObject TopEntries(Dictionary<string, int> values, int max)
    {
        var list = new List<KeyValuePair<string, int>>(values);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var result = new JObject();
        for (int i = 0; i < list.Count && i < max; i++) result[list[i].Key] = list[i].Value;
        return result;
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private void Caption(string text, float seconds)
    {
        if (commands != null) commands.ShowCaption(text, seconds);
        else Debug.Log($"GeminiLiveAssistant: {text}");
    }

    private static void PlayUISound(AudioClip clip)
    {
        if (Camera.main == null) return;
        Transform head = Camera.main.transform;
        UISounds.Play(clip, head.position + head.forward * 0.5f);
    }

    /// <summary>16-bit mono PCM to a .wav file.</summary>
    private static void WriteWav(string path, byte[] pcm, int rate)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);      // PCM
            writer.Write((short)1);      // mono
            writer.Write(rate);
            writer.Write(rate * 2);      // bytes per second
            writer.Write((short)2);      // block align
            writer.Write((short)16);     // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
    }

    private static string Escape(string text) => (text ?? "").Replace("<", "‹").Replace(">", "›");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";
}
