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
        R("Re-centre (seated)", "R3 (right stick click)", "Controls page button", "H"),
        Section("Panels"),
        R("Press a button / row", "point + trigger", "point + pinch / poke", "click"),
        R("Change the month", "drag slider, Prev / Next", "same", "drag / click"),
        R("Open / close a city", "trigger on city / its label", "pinch on it", "click"),
        R("Close the info card", "X on the card", "same", "X / Esc"),
    };

    private static Row Section(string title) => new Row { action = title };

    private static Row R(string action, string controller, string hands, string editor) =>
        new Row { action = action, controller = controller, hands = hands, editor = editor };
}
