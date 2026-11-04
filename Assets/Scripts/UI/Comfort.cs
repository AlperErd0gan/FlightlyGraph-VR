using UnityEngine;

/// <summary>
/// Comfort settings. Reduce motion (dashboard → Controls) keeps the feedback but drops large
/// movement across the view: the map / dome switches jump while the routes are faded out
/// instead of flying the airports, the intro shows the graph without unfolding, the route
/// animation skips its travelling light, city groups open at once, and panels fade without
/// growing. Remembered between sessions (PlayerPrefs).
/// </summary>
public static class Comfort
{
    private const string ReduceMotionKey = "FlightlyVR.ReduceMotion";
    private static bool? reduceMotion;

    /// <summary>Raised when a setting changes.</summary>
    public static event System.Action Changed;

    public static bool ReduceMotion
    {
        get
        {
            if (reduceMotion == null) reduceMotion = PlayerPrefs.GetInt(ReduceMotionKey, 0) == 1;
            return reduceMotion.Value;
        }
        set
        {
            if (ReduceMotion == value) return;
            reduceMotion = value;
            PlayerPrefs.SetInt(ReduceMotionKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log("Comfort: reduce motion " + (value ? "on" : "off"));
            Changed?.Invoke();
        }
    }
}
