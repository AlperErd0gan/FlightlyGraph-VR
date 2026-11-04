using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Short controller vibrations for the moments the XR rig's own feedback does not cover:
/// on the event that causes them, in the same frame as their sound, and only where they mean
/// something (no pulse on hover). The XRI Starter Assets rig already pulses through
/// SimpleHapticFeedback when its Near-Far interactors hover or select an XR interactable,
/// such as the airports; UI buttons, aircraft and routes (picked by ray, no interactable)
/// and a found connection get their pulse here. Pulses go through the controller's
/// HapticImpulsePlayer, so they follow the rig's haptic output setup; hand tracking and rigs
/// without a HapticImpulsePlayer simply get none.
/// </summary>
public static class ControllerHaptics
{
    /// <summary>A press or a pick: a button, an aircraft, a route.</summary>
    public const float TapAmplitude = 0.3f;
    public const float TapSeconds = 0.04f;
    /// <summary>A result worth noticing: the connection between two airports was found.</summary>
    public const float FoundAmplitude = 0.6f;
    public const float FoundSeconds = 0.1f;

    /// <summary>Pulses the controller of `interactor`; false when it has no haptic output.</summary>
    public static bool Pulse(Component interactor, float amplitude, float seconds)
    {
        if (interactor == null) return false;
        HapticImpulsePlayer player = interactor.GetComponentInParent<HapticImpulsePlayer>(true);
        return player != null && player.isActiveAndEnabled && player.SendHapticImpulse(amplitude, seconds);
    }

    /// <summary>Pulses the controller behind a UI pointer event (none for the mouse in the Editor).</summary>
    public static bool Pulse(BaseEventData data, float amplitude, float seconds)
    {
        return data is TrackedDeviceEventData tracked && Pulse(tracked.interactor as Component, amplitude, seconds);
    }

    /// <summary>A tap pulse for selecting an XR interactable, unless the rig's SimpleHapticFeedback already plays one on select.</summary>
    public static bool SelectPulse(Component interactor)
    {
        if (interactor == null) return false;
        foreach (SimpleHapticFeedback rig in interactor.GetComponentsInChildren<SimpleHapticFeedback>(true))
        {
            if (rig.isActiveAndEnabled && rig.playSelectEntered) return false;
        }
        return Pulse(interactor, TapAmplitude, TapSeconds);
    }
}
