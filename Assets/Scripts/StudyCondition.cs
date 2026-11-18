using System;
using UnityEngine;

/// <summary>
/// Which version of the app runs, for the immersion study: Immersive (the Quest headset,
/// as built) or Desktop (the same scene, data, features and look on a monitor with mouse and
/// keyboard; DesktopMode). Chosen by the experimenter, never by the participant: the launch
/// argument -desktop (or -immersive) in a build, else this component's field in the scene
/// (for the Editor), else Immersive. Participant-facing texts pick their wording with Text().
/// </summary>
public class StudyCondition : MonoBehaviour
{
    public enum Condition { Immersive, Desktop }

    [Tooltip("Version to run when no launch argument (-desktop / -immersive) says otherwise.")]
    public Condition condition = Condition.Immersive;

    private static Condition? current;

    /// <summary>The condition of this run (resolved once, on first use after the scene has loaded).</summary>
    public static Condition Current
    {
        get
        {
            if (current == null) current = Resolve();
            return current.Value;
        }
    }

    public static bool IsDesktop => Current == Condition.Desktop;

    /// <summary>The immersive or the desktop wording of a text, for the current condition.</summary>
    public static string Text(string immersive, string desktop) => IsDesktop ? desktop : immersive;

    /// <summary>Sets the condition directly (tests, or a launcher); null resolves it again.</summary>
    public static void Set(Condition? value) => current = value;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() => current = null; // Editor: Play without domain reload

    private static Condition Resolve()
    {
        Condition result = Condition.Immersive;
        string source = "default";
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, "-desktop", StringComparison.OrdinalIgnoreCase))
            {
                result = Condition.Desktop;
                source = "launch argument";
            }
            else if (string.Equals(arg, "-immersive", StringComparison.OrdinalIgnoreCase))
            {
                result = Condition.Immersive;
                source = "launch argument";
            }
        }
        if (source == "default")
        {
            StudyCondition inScene = FindFirstObjectByType<StudyCondition>(FindObjectsInactive.Include);
            if (inScene != null)
            {
                result = inScene.condition;
                source = "scene (" + inScene.name + ")";
            }
        }
        Debug.Log($"StudyCondition: {result} ({source})");
        return result;
    }
}
