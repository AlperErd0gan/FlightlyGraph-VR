/// <summary>
/// Every control of the app in one table: shown on the dashboard's Controls page and in
/// the guided tour. Keep it in sync with docs/CONTROLS.md (which has the long version).
/// Rows with only an action are section titles.
/// </summary>
public static class ControlsHelp
{
    public struct Row
    {
        public string action;
        public string controller;
        public string hands;
        public string editor;
        public bool IsSection => controller == null;
    }

    public static readonly Row[] Rows =
    {
        Section("Graph"),
        R("Select airport / route", "point + trigger", "point + pinch", "click"),
        R("Route to a 2nd airport", "hold trigger on it", "hold pinch on it", "Shift + click"),
        R("  ...or", "point at it, press A / X", "-", "-"),
        R("Clear the selection", "trigger on empty space", "pinch on empty space", "Esc"),
        R("Teleport", "hold trigger on floor, release", "hold pinch on floor", "-"),
        Section("Views"),
        R("Dashboard", "left menu button", "-", "F1"),
        R("Top routes / regional", "L3 (left stick click)", "left palm up + pinch", "V"),
        R("Timeline", "Timeline (dashboard)", "same", "T"),
        R("City groups on / off", "Clusters page", "same", "C"),
        R("Map / 3D view", "B (or dashboard: Map view)", "dashboard: Map view", "M"),
        R("Live flights", "dashboard: Live flights", "same", "P, [ / ] speed"),
        R("Go to graph centre", "R3 (right stick click)", "Controls page button", "H"),
        Section("Panels"),
        R("Press a button / row", "point + trigger", "point + pinch / poke", "click"),
        R("Change the month", "drag slider, Prev / Next", "same", "drag / click"),
        R("Open / close a city", "trigger on city / its label", "pinch on it", "click"),
        R("Close the info card", "X on the card", "same", "X / Esc"),
    };

    /// <summary>Voice assistant: hold Y (N in the Editor), say it, release. Shown on the Controls page's Voice tab.</summary>
    public static readonly (string say, string does)[] Voice =
    {
        ("\"help\"", "lists what you can say"),
        ("\"show Istanbul\" · \"find FRA\" · \"London\"", "selects the airport, turns you to it"),
        ("\"route from London to Ankara\"", "fewest-stops route"),
        ("\"describe\" · \"what is this\"", "tells about the selection"),
        ("\"clear\" · \"close\"", "clears the selection"),
        ("\"regional view\" · \"top routes\"", "view mode"),
        ("\"map view\" · \"3D view\"", "map of Europe / 3D layout"),
        ("\"filter cargo\" · \"filter Turkey\" · \"reset filters\"", "filters"),
        ("\"play\" · \"show April 2020\" · \"all months\"", "timeline"),
        ("\"open dashboard\" · \"open insights\"", "dashboard and its pages"),
        ("\"group cities\" · \"ungroup cities\"", "city groups"),
        ("\"live flights\" · \"faster\" · \"stop the flights\"", "flight simulation"),
        ("\"go to the centre\"", "same as R3"),
        ("\"start tour\" · \"next\" · \"stop tour\"", "guided tour"),
        ("\"stop\" · \"quiet\"", "stops speech and the timeline"),
    };

    private static Row Section(string title) => new Row { action = title };

    private static Row R(string action, string controller, string hands, string editor) =>
        new Row { action = action, controller = controller, hands = hands, editor = editor };
}
