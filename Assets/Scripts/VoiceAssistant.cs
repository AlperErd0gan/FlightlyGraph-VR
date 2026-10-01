using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Voice assistant "Rebecca" (TASKS B2), through the Meta Voice SDK (Wit.ai speech-to-text,
/// the same Wit app as the narration): "show Istanbul", "route from London to Ankara",
/// "filter cargo", "open the dashboard", "help".
/// PushToTalk (default): hold Y on the left controller (N in the Editor) and speak; only
/// that goes to Wit, and no name is needed. AlwaysListening: the microphone stays open and
/// only sentences starting with her name ("Rebecca, show Istanbul") are acted on; saying
/// only "Rebecca" makes her wait a few seconds for the command. Note that in this mode
/// everything said near the microphone is sent to Wit; the name is checked on its text.
/// Works wherever the Voice SDK does (Quest, PC over Link, Editor): the SDK's
/// AppVoiceExperience is called by reflection, like the TTSSpeaker in NodeNarrator, so this
/// compiles without the SDK. Commands are matched against the loaded data (airport names,
/// cities, codes, segments, countries, months), so they work with any dataset.
/// A small caption in view shows what was heard and what was done.
/// Testing without speech: type a sentence in Test Phrase and press F8 (or use the
/// component's context menu).
/// </summary>
public class VoiceAssistant : MonoBehaviour
{
    [Header("Scene (found automatically if unset)")]
    public GraphLoader graph;
    public GraphSelector selector;
    public DataDashboard dashboard;
    public GraphViewMode viewMode;
    public ConnectionFinder connections;
    public GuidedTour tour;
    public NodeNarrator narrator;
    public TimelinePanel timeline;
    public CityClusters cityClusters;
    public GraphRecenter recenter;
    public GeoMapView geoMap;
    public FlightPanel flightPanel;
    [Tooltip("AppVoiceExperience component (Meta Voice SDK) with the Wit configuration. Found in the scene by type name if unset.")]
    public Component voice;

    [Header("Assistant")]
    public string assistantName = "Rebecca";
    [Tooltip("Spellings speech recognition may produce for the name (lower case).")]
    public string[] nameVariants = { "rebecca", "rebekah", "rebeca", "rebecka", "rebekka", "rebeka", "rebecca's", "becca" };
    public enum ListenMode { PushToTalk, AlwaysListening }
    [Tooltip("PushToTalk: only what is said while the button is held goes to Wit. AlwaysListening: the microphone stays open, " +
             "everything heard is sent to Wit, and only sentences starting with the name are acted on.")]
    public ListenMode mode = ListenMode.PushToTalk;
    [Tooltip("Hold to talk (Input System path). Default: Y on the left controller.")]
    public string pushToTalkBinding = "<XRController>{LeftHand}/{SecondaryButton}";
    [Tooltip("Hold to talk in the Editor.")]
    public string pushToTalkKey = "<Keyboard>/n";
    [Tooltip("Push to talk: seconds to wait for Wit's text after the button is released.")]
    public float resultTimeout = 8f;
    [Tooltip("AlwaysListening: after only the name is heard, the next sentence within this many seconds is taken as the command.")]
    public float awakeSeconds = 6f;
    [Tooltip("Answer short commands aloud through the narrator (selections are described by the narrator anyway).")]
    public bool speakReplies = true;
    [Tooltip("Turn towards the airport / route that a command shows.")]
    public bool turnToResult = true;
    [Tooltip("Optional second TTS Speaker (its object or the component) for the \"Jarvis, I'm home\" easter egg, e.g. a calm British male " +
             "voice preset. Empty = the narrator's voice.")]
    public Component jarvisSpeaker;

    [Header("Caption")]
    public float captionDistance = 1.2f;
    [Tooltip("Height of the caption relative to the eyes (m); the tour caption sits lower.")]
    public float captionHeight = -0.24f;
    [Tooltip("Seconds a result stays on the caption.")]
    public float captionSeconds = 4f;

    [Header("Testing")]
    [Tooltip("Sentence run by F8 / the context menu, as if it had been heard.")]
    public string testPhrase = "Rebecca show Istanbul";

    /// <summary>True while the assistant keeps the microphone open for her name.</summary>
    public bool Listening { get; private set; }

    // Voice SDK by reflection.
    private MethodInfo activateMethod;
    private PropertyInfo activeProperty;
    private PropertyInfo requestActiveProperty;
    private bool voiceReady;
    private bool requestOpen;
    private float nextActivation;

    private float awakeUntil;
    private InputAction testAction;
    private InputAction talkAction;
    private bool holding;
    private float acceptUntil;

    // Caption.
    private Canvas captionCanvas;
    private TextMeshProUGUI captionText;
    private float captionUntil;
    private bool captionPlaced;

    // Data vocabulary, built on first use.
    private List<string> segmentNames;

    private static readonly string[] Months =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
    private static readonly string[] Fillers =
        { "please", "can you", "could you", "would you", "will you", "i want to", "i want", "i would like to", "lets", "let us",
          "hey", "ok", "okay", "and", "um", "uh", "now", "just" };
    private static readonly string[] AirportNoise = { "airport", "international", "intl", "regional", "airfield" };

    private void Awake()
    {
        testAction = new InputAction("Voice Test Phrase", InputActionType.Button, "<Keyboard>/f8");
        talkAction = new InputAction("Voice Push To Talk", InputActionType.Button);
        if (!string.IsNullOrEmpty(pushToTalkBinding)) talkAction.AddBinding(pushToTalkBinding);
        if (!string.IsNullOrEmpty(pushToTalkKey)) talkAction.AddBinding(pushToTalkKey);
    }

    private void OnEnable()
    {
        testAction.Enable();
        talkAction.Enable();
    }

    private void OnDisable()
    {
        testAction.Disable();
        talkAction.Disable();
    }

    private void Start()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (selector == null) selector = FindFirstObjectByType<GraphSelector>();
        if (dashboard == null) dashboard = FindFirstObjectByType<DataDashboard>();
        if (viewMode == null) viewMode = FindFirstObjectByType<GraphViewMode>();
        if (connections == null) connections = FindFirstObjectByType<ConnectionFinder>();
        if (tour == null) tour = FindFirstObjectByType<GuidedTour>();
        if (narrator == null) narrator = FindFirstObjectByType<NodeNarrator>();

        BuildCaption();
        voiceReady = HookVoice();
        Listening = voiceReady && mode == ListenMode.AlwaysListening;
        Show(!voiceReady ? "Voice input unavailable: no AppVoiceExperience in the scene."
             : mode == ListenMode.PushToTalk ? "Hold <b>Y</b> and speak. Say <b>\"help\"</b> to hear what I can do."
             : $"Say <b>\"{assistantName}, help\"</b> to hear what I can do.", voiceReady ? 6f : 10f);
    }

    private void OnDestroy()
    {
        testAction.Dispose();
        talkAction.Dispose();
        if (captionCanvas != null) Destroy(captionCanvas.gameObject);
    }

    private void Update()
    {
        if (testAction.WasPressedThisFrame()) RunTestPhrase();
        if (mode == ListenMode.PushToTalk)
        {
            if (talkAction.WasPressedThisFrame()) StartTalking();
            if (holding && talkAction.WasReleasedThisFrame()) StopTalking();
        }
        else if (voiceReady && Listening && !VoiceBusy() && Time.time >= nextActivation) Activate();
        UpdateCaption();
    }

    /// <summary>Starts or stops listening for the assistant's name.</summary>
    public void SetListening(bool value)
    {
        Listening = value && voiceReady;
        if (!Listening) InvokeVoice("Deactivate");
    }

    [ContextMenu("Run Test Phrase")]
    public void RunTestPhrase()
    {
        acceptUntil = Time.time + 1f; // as if said with the button held
        HandleUtterance(testPhrase);
    }

    /// <summary>Push to talk pressed: the microphone sends to Wit until the button is released.</summary>
    private void StartTalking()
    {
        if (!voiceReady)
        {
            Show("Voice input unavailable: no AppVoiceExperience in the scene.", 4f);
            return;
        }
        holding = true;
        if (narrator != null) narrator.Stop(); // do not record the narration
        if (!InvokeVoice("ActivateImmediately")) Activate();
        Show("Listening... release <b>Y</b> when done.", 30f);
        PlaySound(UISounds.Open);
    }

    private void StopTalking()
    {
        holding = false;
        acceptUntil = Time.time + resultTimeout;
        InvokeVoice("Deactivate"); // stop recording; Wit answers with the full text
        Show("<color=#9AA6BA>...</color>", resultTimeout);
    }

    // ---- Voice SDK ---------------------------------------------------------------

    private bool HookVoice()
    {
        if (voice == null) voice = FindComponentByTypeName("AppVoiceExperience");
        if (voice == null)
        {
            Debug.LogWarning("VoiceAssistant: no AppVoiceExperience in the scene (Meta Voice SDK); voice commands disabled. " +
                             "Test Phrase / F8 still work.");
            return false;
        }

        Type type = voice.GetType();
        activateMethod = type.GetMethod("Activate", Type.EmptyTypes);
        activeProperty = BoolProperty(type, "Active");
        requestActiveProperty = BoolProperty(type, "IsRequestActive");
        object events = GetMember(voice, "VoiceEvents") ?? GetMember(voice, "events");
        if (activateMethod == null || events == null)
        {
            Debug.LogWarning($"VoiceAssistant: {type.Name} has no Activate() / VoiceEvents in this Voice SDK version; voice commands disabled.");
            return false;
        }

        bool heard = false;
        if (GetMember(events, "OnFullTranscription") is UnityEvent<string> full) { full.AddListener(HandleUtterance); heard = true; }
        if (GetMember(events, "OnPartialTranscription") is UnityEvent<string> partial) partial.AddListener(OnPartialTranscription);
        if (GetMember(events, "OnError") is UnityEvent<string, string> error) error.AddListener(OnVoiceError);
        foreach (string name in new[] { "OnRequestCompleted", "OnStoppedListening", "OnAborted" })
        {
            if (GetMember(events, name) is UnityEvent ended) ended.AddListener(OnRequestEnded);
        }
        if (!heard)
        {
            Debug.LogWarning("VoiceAssistant: VoiceEvents.OnFullTranscription not found in this Voice SDK version; voice commands disabled.");
            return false;
        }
        Debug.Log($"VoiceAssistant: listening through {type.Name} on '{voice.gameObject.name}'");
        return true;
    }

    /// <summary>The SDK is listening or still waiting for Wit's answer.</summary>
    private bool VoiceBusy()
    {
        bool? active = activeProperty != null ? (bool)activeProperty.GetValue(voice) : (bool?)null;
        bool? request = requestActiveProperty != null ? (bool)requestActiveProperty.GetValue(voice) : (bool?)null;
        if (active == null && request == null) return requestOpen;
        return active == true || request == true;
    }

    private void Activate()
    {
        // Activate() opens the microphone and only sends audio to Wit once someone speaks;
        // after the sentence (or a silent timeout) it ends and is started again here.
        requestOpen = true;
        nextActivation = Time.time + 0.5f;
        try { activateMethod.Invoke(voice, null); }
        catch (Exception ex)
        {
            requestOpen = false;
            nextActivation = Time.time + 3f;
            Debug.LogWarning($"VoiceAssistant: Activate failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private void OnRequestEnded()
    {
        requestOpen = false;
        nextActivation = Mathf.Max(nextActivation, Time.time + 0.2f);
    }

    private void OnVoiceError(string error, string message)
    {
        requestOpen = false;
        nextActivation = Time.time + 2f; // do not hammer Wit when offline
        Debug.LogWarning($"VoiceAssistant: Wit error {error}: {message}");
    }

    private void OnPartialTranscription(string text)
    {
        // Only show live text once the sentence is addressed to the assistant.
        string norm = Normalize(text);
        if (Pushed || WakeIndex(norm.Split(' ')) >= 0 || Time.time < awakeUntil) Show($"<color=#9AA6BA>{Escape(text)}...</color>", 2f);
    }

    /// <summary>Calls a parameterless SDK method; false if it does not exist or failed.</summary>
    private bool InvokeVoice(string method)
    {
        if (voice == null) return false;
        MethodInfo m = voice.GetType().GetMethod(method, Type.EmptyTypes);
        if (m == null) return false;
        try
        {
            m.Invoke(voice, null);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"VoiceAssistant: {method} failed: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>Push to talk: the button is held, or was just released and Wit's answer is pending.</summary>
    private bool Pushed => mode == ListenMode.PushToTalk && (holding || Time.time < acceptUntil);

    private static Component FindComponentByTypeName(string typeName)
    {
        foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            for (Type t = behaviour.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (t.Name == typeName) return behaviour;
            }
        }
        return null;
    }

    private static object GetMember(object target, string name)
    {
        if (target == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
            FieldInfo f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(target);
        }
        return null;
    }

    private static PropertyInfo BoolProperty(Type type, string name)
    {
        PropertyInfo property = type.GetProperty(name);
        return property != null && property.PropertyType == typeof(bool) ? property : null;
    }

    // ---- Sentences -----------------------------------------------------------------

    /// <summary>Handles one recognised sentence: acts on it if it is addressed to the assistant.</summary>
    public void HandleUtterance(string text)
    {
        string norm = Normalize(text);
        if (norm.Length == 0) return;
        string[] words = norm.Split(' ');
        int wake = WakeIndex(words);

        bool pushed = Pushed;
        if (pushed && !holding) acceptUntil = 0f; // one sentence per press

        // Easter egg: "Jarvis, I'm home" (no name needed in either mode).
        if ((pushed || mode == ListenMode.AlwaysListening) &&
            Regex.IsMatch(norm, @"^(?:(?:hey|ok|okay|rebecca) )?(?:jarvis|jarvas|jervis|travis) (?:i am|im|i m) (?:back )?home$"))
        {
            Debug.Log($"VoiceAssistant: heard \"{text}\" -> easter egg");
            Show("<b>Hello, Mr. Stark.</b>", captionSeconds);
            if (!SpeakWith(jarvisSpeaker, "Hello, Mister Stark.")) Say("Hello, Mister Stark.", force: true);
            PlaySound(UISounds.Open);
            return;
        }
        string command;
        if (wake >= 0) command = string.Join(" ", words, wake + 1, words.Length - wake - 1);
        else if (pushed || Time.time < awakeUntil) command = norm;
        else
        {
            Debug.Log($"VoiceAssistant: ignored (no '{assistantName}'): \"{text}\"");
            return;
        }

        command = StripFillers(command);
        Debug.Log($"VoiceAssistant: heard \"{text}\" -> command \"{command}\"");
        if (command.Length == 0 && pushed)
        {
            Show("I didn't hear a command.", 3f);
            return;
        }
        if (command.Length == 0)
        {
            awakeUntil = Time.time + awakeSeconds;
            Show("Listening...", awakeSeconds);
            PlaySound(UISounds.Open);
            return;
        }
        awakeUntil = 0f;

        string reply;
        bool understood;
        try { understood = Execute(command, out reply); }
        catch (Exception ex)
        {
            Debug.LogError($"VoiceAssistant: command \"{command}\" failed: {ex}");
            understood = false;
            reply = "Something went wrong with that command.";
        }

        if (!understood && string.IsNullOrEmpty(reply)) reply = $"Sorry, I didn't get \"{command}\". {Capitalise(Hint("help"))}.";
        Show($"<color=#9AA6BA>\"{Escape(command)}\"</color>\n{reply}", captionSeconds + (reply.Length > 80 ? 3f : 0f));
        PlaySound(understood ? UISounds.Click : UISounds.Close);
    }

    /// <summary>How to say a command in the current mode: hold Y and say "help" / say "Rebecca, help".</summary>
    private string Hint(string command) =>
        mode == ListenMode.PushToTalk ? $"hold Y and say \"{command}\"" : $"say \"{assistantName}, {command}\"";

    private static string Capitalise(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;

    /// <summary>Index of the assistant's name among the words, or -1.</summary>
    private int WakeIndex(string[] words)
    {
        string name = Normalize(assistantName);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] == name || Array.IndexOf(nameVariants, words[i]) >= 0) return i;
        }
        return -1;
    }

    private static string StripFillers(string command)
    {
        bool changed = true;
        while (changed && command.Length > 0)
        {
            changed = false;
            foreach (string filler in Fillers)
            {
                if (command == filler) { command = ""; changed = true; break; }
                if (command.StartsWith(filler + " ", StringComparison.Ordinal)) { command = command.Substring(filler.Length + 1); changed = true; break; }
            }
        }
        if (command.EndsWith(" please", StringComparison.Ordinal)) command = command.Substring(0, command.Length - 7);
        return command.Trim();
    }

    // ---- Commands ------------------------------------------------------------------

    private bool Execute(string c, out string reply)
    {
        reply = null;
        Match m;

        // Help and the tour first: they work even while the tour locks the input.
        if (Is(c, @"^(help|what can i say|what can you do|commands|show commands)$"))
        {
            reply = "Try: show Istanbul · route from London to Ankara · filter cargo · reset filters · " +
                    "regional view · top routes · map view · 3D view · live flights · play timeline · show April 2020 · group cities · " +
                    "open dashboard · open insights · start tour · go to centre · clear · stop.";
            Say("You can say: show an airport, route from one city to another, filter cargo, regional view, " +
                "play timeline, open dashboard or start tour.");
            return true;
        }
        if (tour != null && Is(c, @"^((start|begin|play|run)( the)?( guided)? tour|(guided )?tour)$"))
        {
            if (dashboard != null && dashboard.IsOpen) dashboard.Close();
            tour.StartTour();
            reply = "Starting the guided tour.";
            return true;
        }
        if (tour != null && tour.IsRunning)
        {
            if (Is(c, @"^((stop|end|exit|cancel|quit|close)( the)?( guided)? tour|stop|cancel|exit)$"))
            {
                tour.StopTour();
                reply = "Tour stopped.";
                return true;
            }
            if (Is(c, @"^(next|next step|continue|skip|go on)$"))
            {
                tour.NextStep();
                reply = "Next step.";
                return true;
            }
        }
        if (GuidedTour.InputLocked)
        {
            reply = $"The tour is running: {Hint("next")} or \"stop tour\".";
            return false;
        }

        if (Is(c, @"^(stop|quiet|silence|be quiet|stop talking|shut up|enough)$"))
        {
            if (narrator != null) narrator.Stop();
            if (Timeline != null && Timeline.IsPlaying) Timeline.SetPlaying(false);
            if (Flights != null && Flights.IsOpen) Flights.SetPlaying(false);
            reply = "OK.";
            return true;
        }

        // Dashboard and its pages.
        if (Is(c, @"^(open|show)( the)? (dashboard|menu)$"))
        {
            if (dashboard == null) return NotAvailable("dashboard", out reply);
            dashboard.Open();
            reply = "Dashboard open.";
            return true;
        }
        if (Is(c, @"^(close|hide)( the)? (dashboard|menu)$"))
        {
            if (dashboard == null) return NotAvailable("dashboard", out reply);
            dashboard.Close();
            reply = "Dashboard closed.";
            return true;
        }
        // "show clusters" means the regional view below, so these three pages need "open" or "page".
        if ((m = Regex.Match(c, @"^(?:open|go to|show)(?: the)? (overview|insights?|filters?|controls|traffic mix|find|search)(?: page| tab)?$")).Success ||
            (m = Regex.Match(c, @"^(?:open|go to)(?: the)? (airports|routes|clusters)(?: page| tab)?$")).Success ||
            (m = Regex.Match(c, @"^(?:show)(?: the)? (airports|routes|clusters) (?:page|tab)$")).Success)
        {
            if (dashboard == null) return NotAvailable("dashboard", out reply);
            DataDashboard.Tab tab = TabFor(m.Groups[1].Value);
            dashboard.OpenTab(tab);
            reply = $"{tab} page.";
            return true;
        }

        // View mode.
        if (Is(c, @"^((show )?(the )?(regional|regions|region|clusters?|communities)( view| mode)?|(switch|change|go) to (regional|regions|clusters?)( view| mode)?)$"))
        {
            if (viewMode == null || graph == null) return NotAvailable("view mode", out reply);
            viewMode.Apply(GraphViewMode.ViewMode.Regional, true);
            reply = $"Regional view: {graph.Communities.Count} clusters.";
            Say("Regional view.");
            return true;
        }
        if (Is(c, @"^((show )?(the )?(top|busiest|main) routes( view| mode)?|(normal|default|main|route) view|(switch|change|go) to (top|busiest) routes( view)?)$"))
        {
            if (viewMode == null) return NotAvailable("view mode", out reply);
            viewMode.Apply(GraphViewMode.ViewMode.TopRoutes, true);
            reply = "Top routes view.";
            Say(reply);
            return true;
        }
        if (Is(c, @"^(switch|change|toggle)( the)? (view|mode)$"))
        {
            if (viewMode == null) return NotAvailable("view mode", out reply);
            bool regional = viewMode.Mode != GraphViewMode.ViewMode.Regional;
            viewMode.Apply(regional ? GraphViewMode.ViewMode.Regional : GraphViewMode.ViewMode.TopRoutes, true);
            reply = regional ? "Regional view." : "Top routes view.";
            Say(reply);
            return true;
        }

        // Map of Europe (GeoMapView) and back to the 3D layout.
        if (Is(c, @"^((show|open|switch to|go to|change to)( the)? (map|map view|geographic view)|map( view)?|(show|put)( the)? (airports|them|it) on (the|a) map)$"))
        {
            if (GeoMap == null || !GeoMap.Ready) return NotAvailable("map view", out reply);
            if (dashboard != null && dashboard.IsOpen) dashboard.Close();
            GeoMap.SetMap(true);
            reply = $"Map view: {GeoMap.OutsideCount} airports beyond Europe are on the outer ring.";
            Say("Map view.");
            return true;
        }
        if (Is(c, @"^((show|open|switch to|go to|back to|change to)( the)? (3 ?d|three d|network|graph)( view| layout)?|(3 ?d|three d|network)( view| layout)|(close|hide|leave)( the)? map)$"))
        {
            if (GeoMap == null || !GeoMap.Ready) return NotAvailable("map view", out reply);
            GeoMap.SetMap(false);
            reply = "3D view.";
            Say(reply);
            return true;
        }

        // Selection, re-centre, city groups.
        if (Is(c, @"^((clear|reset|remove|deselect|unselect)( the)?( selection)?|clear all|close|close (it|this|the card|the panel))$"))
        {
            if (selector != null) selector.ClearSelection();
            reply = "Selection cleared.";
            return true;
        }
        if (Is(c, @"^(re ?cent(er|re)( the graph)?|cent(er|re)( me)?|(go|take me|move me|bring me|teleport me|teleport)( back)? to( the)?( graph)? (cent(er|re)|middle))$"))
        {
            GraphRecenter r = Recenter;
            if (r == null) return NotAvailable("re-centre", out reply);
            r.Recenter();
            reply = "Moved you to the graph centre.";
            return true;
        }
        if (Is(c, @"^(group|merge|combine|collapse)( the)? cities$"))
        {
            if (Cities == null) return NotAvailable("city groups", out reply);
            Cities.SetGrouped(true);
            reply = $"Cities grouped ({Cities.CityCount}).";
            Say("Cities grouped.");
            return true;
        }
        if (Is(c, @"^(ungroup|split|separate|expand|uncollapse)( the)? cities$"))
        {
            if (Cities == null) return NotAvailable("city groups", out reply);
            Cities.SetGrouped(false);
            reply = "Cities separated.";
            Say(reply);
            return true;
        }

        // Live flights (FlightPanel): open / close, speed; play / pause while it is open.
        if (Is(c, @"^((show|open|start|play|watch)( the)? (live flights|flights live|flight simulation|simulation|planes|aircraft|air traffic)|live flights|simulate( the)? flights)$"))
        {
            if (Flights == null || !Flights.Available) return NotAvailable("live flights", out reply);
            if (dashboard != null && dashboard.IsOpen) dashboard.Close();
            Flights.Open();
            reply = "Live flights: " + Flights.simulator.SimUtc.ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC.";
            Say("Live flights.");
            return true;
        }
        if (Is(c, @"^((close|hide|stop|end)( the)? (live flights|flights|flight simulation|simulation|planes|aircraft))$"))
        {
            if (Flights == null || !Flights.IsOpen) return NotAvailable("live flights", out reply);
            Flights.Close();
            reply = "Live flights closed.";
            return true;
        }
        if (Flights != null && Flights.IsOpen)
        {
            if (Is(c, @"^(faster|speed up|go faster|quicker)$")) { Flights.ChangeSpeed(+1); reply = "Faster: " + FlightPanel.SpeedLabel(Flights.simulator.Speed) + "."; return true; }
            if (Is(c, @"^(slower|slow down|go slower)$")) { Flights.ChangeSpeed(-1); reply = "Slower: " + FlightPanel.SpeedLabel(Flights.simulator.Speed) + "."; return true; }
            if (Is(c, @"^(play|resume|continue|go)( the)?( flights)?$")) { Flights.SetPlaying(true); reply = "Playing."; return true; }
            if (Is(c, @"^(pause|freeze|wait)( the)?( flights)?$")) { Flights.SetPlaying(false); reply = "Paused."; return true; }
        }

        // Timeline.
        if (Is(c, @"^(open|show)( the)? (timeline|time line|time slider)$"))
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            Timeline.Open();
            reply = "Timeline open.";
            return true;
        }
        if (Is(c, @"^((close|hide)( the)? (timeline|time line)|(show )?all (months|time|years)|all time)$"))
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            Timeline.Close();
            graph.SetPeriod(-1);
            reply = "All months.";
            return true;
        }
        if (Is(c, @"^(play|start|run)( the)?( timeline| time line| time| over time| months)?$|^play over time$"))
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            Timeline.Open();
            Timeline.SetPlaying(true);
            reply = "Playing the timeline.";
            return true;
        }
        if (Is(c, @"^(pause|stop|freeze)( the)?( timeline| time line| time| playing)?$"))
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            Timeline.SetPlaying(false);
            reply = graph.HasPeriod ? $"Paused at {graph.PeriodName(graph.Period)}." : "Paused.";
            return true;
        }
        if (Is(c, @"^(next|previous|last) month$|^(forward|back|backward)$"))
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            if (!Timeline.IsOpen) Timeline.Open();
            Timeline.Step(c.StartsWith("next", StringComparison.Ordinal) || c == "forward" ? 1 : -1);
            reply = graph.HasPeriod ? graph.PeriodName(graph.Period) : "All months.";
            return true;
        }
        if ((m = Regex.Match(c, @"^(?:(?:show|go to|jump to|open|in)(?: the)? )?(?:(" + string.Join("|", Months) + @") )?(?:of )?(20\d\d|19\d\d)$")).Success)
        {
            if (Timeline == null || !graph.HasTimeAxis) return NoTimeAxis(out reply);
            int month = m.Groups[1].Success ? Array.IndexOf(Months, m.Groups[1].Value) + 1 : 1;
            int period = FindPeriod(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), month);
            if (period < 0)
            {
                reply = $"No data for {m.Groups[2].Value}. The data runs from {graph.PeriodName(0)} to {graph.PeriodName(graph.PeriodCount - 1)}.";
                return false;
            }
            Timeline.Open();
            Timeline.SetPlaying(false);
            graph.SetPeriod(period);
            reply = $"Showing {graph.PeriodName(period)}.";
            return true;
        }

        // Filters.
        if (Is(c, @"^((reset|clear|remove)( all)?( the)? filters?|no filters?|(show )?(everything|all flights|all traffic))$"))
        {
            if (graph == null) return false;
            if (selector != null) selector.ClearSelection();
            graph.SetFilter(null, null, 0);
            reply = "Filters cleared.";
            Say(reply);
            return true;
        }
        if ((m = Regex.Match(c, @"^(?:filter|filter by|filter on|filter to|only|show only|just show)(?: the)? (.+?)(?: flights| traffic| airports| routes)?$")).Success)
        {
            return ApplyVoiceFilter(m.Groups[1].Value, out reply);
        }

        // Routes between two airports.
        if ((m = Regex.Match(c, @"^(?:(?:show|find|what is|whats|give me)(?: me)? )?(?:the )?(?:route|path|connection|way|flight|flights)s? (?:from )?(.+?) (?:to|and|with) (.+)$")).Success ||
            (m = Regex.Match(c, @"^(?:connect|link) (.+?) (?:to|and|with) (.+)$")).Success ||
            (m = Regex.Match(c, @"^(?:fly |go |get )?from (.+?) to (.+)$")).Success ||
            (m = Regex.Match(c, @"^how (?:do i|can i|to|would i) (?:get|fly|go|travel) from (.+?) to (.+)$")).Success)
        {
            return ShowRoute(m.Groups[1].Value, m.Groups[2].Value, out reply);
        }

        // Describe the selection.
        if (Is(c, @"^((describe|explain)( (this|it|that|the selection))?|tell me about (this|it|that|the selection)|what is (this|that)|whats this)$"))
        {
            if (narrator == null || selector == null) return NotAvailable("narration", out reply);
            string text = selector.SelectedNode != null ? narrator.DescribeNode(selector.SelectedNode)
                        : selector.SelectedEdge != null ? narrator.DescribeEdge(selector.SelectedEdge) : null;
            if (text == null)
            {
                reply = "Nothing is selected.";
                return false;
            }
            Say(text, force: true);
            reply = text;
            return true;
        }

        // One airport: "show Istanbul", "where is Frankfurt", or just "Istanbul".
        m = Regex.Match(c, @"^(?:show|find|select|go to|where is|wheres|focus on|focus|look at|zoom to|zoom in on|take me to|highlight|search for|search|open|tell me about|what about)(?: me)?(?: the)? (.+)$");
        string query = m.Success ? m.Groups[1].Value : c;
        GraphNode node = ResolveAirport(query);
        if (node != null)
        {
            if (dashboard != null && dashboard.IsOpen) dashboard.Close();
            selector.SelectNode(node);
            TurnToward(node.transform.position);
            reply = $"{node.label}" + (node.data != null && !string.IsNullOrEmpty(node.data.city) ? $" · {node.data.city}" : "");
            return true;
        }
        if (m.Success)
        {
            reply = $"I couldn't find an airport called \"{query}\".";
            return false;
        }
        return false;
    }

    private bool ApplyVoiceFilter(string query, out string reply)
    {
        query = Regex.Replace(query, @"^(?:country|segment|market segment|market) ", "");
        string segment = FindSegment(query);
        if (segment != null)
        {
            if (selector != null) selector.ClearSelection();
            graph.SetFilter(segment, graph.FilterCountry, graph.FilterMinWeight);
            reply = $"Filter: {segment} flights.";
            Say($"Showing {segment} flights.");
            return true;
        }
        string country = FindCountry(query);
        if (country != null)
        {
            if (selector != null) selector.ClearSelection();
            graph.SetFilter(graph.FilterSegment, country, graph.FilterMinWeight);
            reply = $"Filter: routes touching {country}.";
            Say($"Showing {country}.");
            return true;
        }
        reply = SegmentNames.Count > 0
            ? $"No segment or country \"{query}\". Segments: {string.Join(", ", SegmentNames)}."
            : $"No segment or country \"{query}\" in this data.";
        return false;
    }

    private bool ShowRoute(string fromText, string toText, out string reply)
    {
        GraphNode from = ResolveAirport(fromText);
        GraphNode to = ResolveAirport(toText);
        if (from == null || to == null)
        {
            reply = $"I couldn't find {(from == null ? $"\"{fromText}\"" : $"\"{toText}\"")}.";
            return false;
        }
        if (from == to)
        {
            reply = $"Both are {from.label}.";
            return false;
        }
        if (connections == null || selector == null) return NotAvailable("routes", out reply);
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (!connections.TryFindPath(from, to, nodes, edges))
        {
            reply = $"No route between {from.ShortCode} and {to.ShortCode} in this graph.";
            return false;
        }
        if (dashboard != null && dashboard.IsOpen) dashboard.Close();
        selector.SelectPath(nodes, edges);
        TurnToward(edges.Count > 0 ? edges[edges.Count / 2].Midpoint : to.transform.position);
        var codes = new List<string>();
        foreach (GraphNode n in nodes) codes.Add(n.ShortCode);
        reply = (edges.Count == 1 ? "Direct: " : $"{edges.Count - 1} stop{(edges.Count == 2 ? "" : "s")}: ") + string.Join(" > ", codes);
        return true;
    }

    private bool NotAvailable(string what, out string reply)
    {
        reply = $"The {what} is not available in this scene.";
        return false;
    }

    private bool NoTimeAxis(out string reply)
    {
        reply = "This dataset has no months, so there is no timeline.";
        return false;
    }

    private static bool Is(string command, string pattern) => Regex.IsMatch(command, pattern);

    private static DataDashboard.Tab TabFor(string word)
    {
        switch (word.Replace(" page", "").Replace(" tab", ""))
        {
            case "insight":
            case "insights": return DataDashboard.Tab.Insights;
            case "filter":
            case "filters": return DataDashboard.Tab.Filters;
            case "controls": return DataDashboard.Tab.Controls;
            case "traffic mix": return DataDashboard.Tab.TrafficMix;
            case "find":
            case "search": return DataDashboard.Tab.Find;
            case "airports": return DataDashboard.Tab.Airports;
            case "routes": return DataDashboard.Tab.Routes;
            case "clusters": return DataDashboard.Tab.Clusters;
            default: return DataDashboard.Tab.Overview;
        }
    }

    // ---- Matching against the data ---------------------------------------------------

    /// <summary>
    /// Best airport for spoken words: code ("IST", "i s t", "LTFM"), airport name, or city
    /// (the city's busiest airport); small misrecognitions are tolerated.
    /// </summary>
    private GraphNode ResolveAirport(string spoken)
    {
        if (graph == null || !graph.IsLoaded) return null;
        string q = Normalize(spoken);
        foreach (string noise in AirportNoise) q = Regex.Replace(q, $@"\b{noise}\b", " ");
        q = Regex.Replace(q, @"\s+", " ").Trim();
        if (q.StartsWith("the ", StringComparison.Ordinal)) q = q.Substring(4);
        if (q.Length < 2) return null;
        // Spelled codes: "i s t" -> "ist".
        string compact = Regex.IsMatch(q, @"^([a-z0-9] ){2,3}[a-z0-9]$") ? q.Replace(" ", "") : q;

        GraphNode best = null;
        float bestScore = 0f;
        foreach (GraphNode node in graph.Nodes.Values)
        {
            float score = AirportScore(node, q, compact);
            if (score > bestScore || (score == bestScore && score > 0f && best != null && node.value > best.value))
            {
                best = node;
                bestScore = score;
            }
        }
        return bestScore >= 35f ? best : null;
    }

    private static float AirportScore(GraphNode node, string q, string compact)
    {
        string code = node.ShortCode.ToLowerInvariant();
        string id = (node.id ?? "").ToLowerInvariant();
        if (compact.Length >= 3 && compact.Length <= 4 && (compact == code || compact == id)) return 100f;

        string name = AirportName(node);
        string city = node.data != null ? Normalize(node.data.city ?? "") : "";
        if (name.Length > 0 && q == name) return 95f;
        if (city.Length > 0 && q == city) return 90f;

        float score = 0f;
        if (q.Length >= 4)
        {
            if (ContainsPhrase(name, q)) score = Mathf.Max(score, 70f + Mathf.Min(q.Length, 15));
            if (ContainsPhrase(city, q)) score = Mathf.Max(score, 65f + Mathf.Min(q.Length, 15));
        }
        if (city.Length >= 4 && ContainsPhrase(q, city)) score = Mathf.Max(score, 60f + Mathf.Min(city.Length, 15));
        if (name.Length >= 4 && ContainsPhrase(q, name)) score = Mathf.Max(score, 62f + Mathf.Min(name.Length, 15));
        if (score > 0f) return score;

        // Misrecognitions ("frankfort", "istambul"): similarity to the city or the name.
        float similarity = Mathf.Max(Similarity(q, city), Similarity(q, name));
        return similarity >= 0.75f ? 50f * similarity : 0f;
    }

    /// <summary>"İstanbul Airport (IST)" -> "istanbul".</summary>
    private static string AirportName(GraphNode node)
    {
        string label = node.label ?? node.id ?? "";
        int paren = label.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) label = label.Substring(0, paren);
        string name = Normalize(label);
        foreach (string noise in AirportNoise) name = Regex.Replace(name, $@"\b{noise}\b", " ");
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    private List<string> SegmentNames
    {
        get
        {
            if (segmentNames != null) return segmentNames;
            var totals = new Dictionary<string, long>();
            if (graph != null)
            {
                foreach (GraphEdge e in graph.Edges)
                {
                    if (e.data == null || e.data.segments == null) continue;
                    foreach (KeyValuePair<string, int> kv in e.data.segments)
                    {
                        totals.TryGetValue(kv.Key, out long sum);
                        totals[kv.Key] = sum + kv.Value;
                    }
                }
            }
            segmentNames = new List<string>(totals.Keys);
            segmentNames.Sort((a, b) => totals[b].CompareTo(totals[a]));
            return segmentNames;
        }
    }

    /// <summary>Market segment for spoken words ("cargo" -> "All-Cargo", "low cost" -> "Lowcost").</summary>
    private string FindSegment(string spoken)
    {
        string q = Normalize(spoken).Replace(" ", "");
        if (q.Length < 3) return null;
        string best = null;
        float bestScore = 0f;
        foreach (string segment in SegmentNames)
        {
            string s = Normalize(segment).Replace(" ", "");
            float score = s == q ? 3f : s.Contains(q) ? 2f : q.Contains(s) ? 1.5f : Similarity(q, s) >= 0.75f ? 1f : 0f;
            if (score > bestScore) { best = segment; bestScore = score; }
        }
        return best;
    }

    private string FindCountry(string spoken)
    {
        string q = Normalize(spoken);
        if (q.Length < 3 || graph == null) return null;
        string best = null;
        float bestScore = 0f;
        foreach (GraphNode node in graph.Nodes.Values)
        {
            string country = node.data != null ? node.data.country : null;
            if (string.IsNullOrEmpty(country)) continue;
            string c = Normalize(country);
            float score = c == q ? 3f : Similarity(q, c) >= 0.8f ? 1f : 0f;
            if (score > bestScore) { best = country; bestScore = score; }
        }
        return best;
    }

    private int FindPeriod(int year, int month)
    {
        string key = $"{year:0000}-{month:00}";
        for (int i = 0; i < graph.PeriodCount; i++)
        {
            if (graph.PeriodName(i) == key) return i;
        }
        return -1;
    }

    /// <summary>Lower case, no accents ("İstanbul" -> "istanbul"), letters / digits / single spaces.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string decomposed = text.Replace('ı', 'i').Replace('İ', 'I').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch == '\'' || ch == '’') continue; // "what's" -> "whats"
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool ContainsPhrase(string haystack, string needle)
    {
        return haystack.Length > 0 && needle.Length > 0 && (" " + haystack + " ").Contains(" " + needle + " ");
    }

    /// <summary>1 - edit distance / longer length.</summary>
    private static float Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0f;
        if (Mathf.Abs(a.Length - b.Length) > Mathf.Max(a.Length, b.Length) / 3) return 0f;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Mathf.Min(Mathf.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            int[] swap = previous; previous = current; current = swap;
        }
        return 1f - previous[b.Length] / (float)Mathf.Max(a.Length, b.Length);
    }

    // ---- Helpers ---------------------------------------------------------------------

    private FlightPanel Flights
    {
        get
        {
            if (flightPanel == null) flightPanel = dashboard != null && dashboard.flights != null ? dashboard.flights : FindFirstObjectByType<FlightPanel>();
            return flightPanel;
        }
    }

    private TimelinePanel Timeline
    {
        get
        {
            if (timeline == null) timeline = dashboard != null && dashboard.timeline != null ? dashboard.timeline : FindFirstObjectByType<TimelinePanel>();
            return timeline;
        }
    }

    private CityClusters Cities
    {
        get
        {
            if (cityClusters == null) cityClusters = dashboard != null && dashboard.cityClusters != null ? dashboard.cityClusters : FindFirstObjectByType<CityClusters>();
            return cityClusters;
        }
    }

    private GeoMapView GeoMap
    {
        get
        {
            if (geoMap == null) geoMap = dashboard != null && dashboard.geoMap != null ? dashboard.geoMap : FindFirstObjectByType<GeoMapView>();
            return geoMap;
        }
    }

    private GraphRecenter Recenter
    {
        get
        {
            if (recenter == null) recenter = dashboard != null && dashboard.recenter != null ? dashboard.recenter : FindFirstObjectByType<GraphRecenter>();
            return recenter;
        }
    }

    private void Say(string text, bool force = false)
    {
        if ((!speakReplies && !force) || narrator == null || Camera.main == null) return;
        Transform head = Camera.main.transform;
        narrator.SpeakText(text, head.position + head.forward * 0.5f);
    }

    /// <summary>Speaks through a TTSSpeaker found on the given component's object (by reflection); false if there is none.</summary>
    private bool SpeakWith(Component target, string text)
    {
        if (target == null) return false;
        Component speaker = null;
        foreach (MonoBehaviour behaviour in target.GetComponents<MonoBehaviour>())
        {
            for (Type t = behaviour.GetType(); t != null && speaker == null; t = t.BaseType)
            {
                if (t.Name == "TTSSpeaker") speaker = behaviour;
            }
        }
        MethodInfo speak = speaker != null ? speaker.GetType().GetMethod("Speak", new[] { typeof(string) }) : null;
        if (speak == null)
        {
            Debug.LogWarning($"VoiceAssistant: no TTSSpeaker on '{target.name}'; using the narrator's voice.");
            return false;
        }
        if (narrator != null) narrator.Stop();
        speak.Invoke(speaker, new object[] { text });
        return true;
    }

    private static void PlaySound(AudioClip clip)
    {
        if (Camera.main == null) return;
        Transform head = Camera.main.transform;
        UISounds.Play(clip, head.position + head.forward * 0.5f);
    }

    private void TurnToward(Vector3 point)
    {
        if (!turnToResult) return;
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = Camera.main;
        if (origin == null || cam == null) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        Vector3 toPoint = Vector3.ProjectOnPlane(point - cam.transform.position, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f || toPoint.sqrMagnitude < 1e-6f) return;
        origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(forward, toPoint, Vector3.up));
    }

    private static string Escape(string text) => (text ?? "").Replace("<", "‹").Replace(">", "›");

    // ---- Caption ---------------------------------------------------------------------

    private void BuildCaption()
    {
        captionCanvas = UIKit.WorldCanvas("Voice Assistant Caption", new Vector2(CaptionWidth, 120f));
        // Display only: nothing on it may catch XR rays or the mouse.
        foreach (BaseRaycaster raycaster in captionCanvas.GetComponents<BaseRaycaster>()) raycaster.enabled = false;
        Image card = UIKit.Box(captionCanvas.transform, "Card", UIKit.PanelColor, 24f);
        card.raycastTarget = false;
        UIKit.Stretch(card.rectTransform);
        UIKit.Text(captionCanvas.transform, assistantName.ToUpperInvariant(), UIKit.SmallSize, UIKit.AccentColor, 22f, 12f, 200f, 26f);
        captionText = UIKit.Text(captionCanvas.transform, "", UIKit.BodySize, UIKit.TextColor, 22f, 38f, CaptionWidth - 44f, 76f);
        captionText.overflowMode = TextOverflowModes.Overflow;
        captionCanvas.gameObject.SetActive(false);
    }

    private const float CaptionWidth = 760f;

    private void Show(string text, float seconds)
    {
        if (captionText == null) return;
        captionText.text = text;
        // Card grows with the text.
        float textHeight = Mathf.Max(30f, captionText.GetPreferredValues(text, CaptionWidth - 44f, 0f).y);
        UIKit.Place(captionText.rectTransform, 22f, 38f, CaptionWidth - 44f, textHeight);
        ((RectTransform)captionCanvas.transform).sizeDelta = new Vector2(CaptionWidth, textHeight + 54f);
        captionUntil = Time.time + seconds;
        if (!captionCanvas.gameObject.activeSelf)
        {
            captionPlaced = false;
            captionCanvas.gameObject.SetActive(true);
        }
    }

    private void UpdateCaption()
    {
        if (captionCanvas == null || !captionCanvas.gameObject.activeSelf) return;
        if (Time.time > captionUntil)
        {
            captionCanvas.gameObject.SetActive(false);
            return;
        }
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform head = cam.transform;
        Vector3 target = head.position + UIKit.FlatForward(head) * captionDistance + Vector3.up * captionHeight;
        Transform t = captionCanvas.transform;
        t.position = captionPlaced ? Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-4f * Time.deltaTime)) : target;
        captionPlaced = true;
        Vector3 away = t.position - head.position;
        if (away.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
