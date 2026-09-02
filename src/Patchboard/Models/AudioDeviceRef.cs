namespace Patchboard.Models;

/// <summary>
/// A persisted reference to a Windows audio endpoint.
///
/// We store the endpoint <see cref="Id"/> because friendly names change when Windows
/// renames a device or a driver updates. Resanance stores truncated friendly names and
/// loses bindings when they shift; we keep the name only for display and as a fallback
/// match when the id has genuinely disappeared.
/// </summary>
public sealed class AudioDeviceRef
{
    /// <summary>WASAPI endpoint id, e.g. "{0.0.0.00000000}.{guid}". Stable across reboots.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name at the time it was saved. Never used as the primary key.</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>Whether sound is routed to (or captured from) this device.</summary>
    public bool Enabled { get; set; }

    /// <summary>0.0 to 1.0. Applied inside our own mix, never to the Windows device volume.</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>
    /// Output devices only: whether the live microphone is mixed into this device as well
    /// as the sounds.
    ///
    /// Defaults to false, and that default is the safety property. Sending the microphone
    /// to a device the user can hear creates an acoustic feedback loop: voice goes to the
    /// speakers, the speakers reach the microphone, and it builds. Samuel hit exactly this,
    /// because his "Voicemeeter Input" strip is routed to A1 and therefore to his monitor
    /// speakers. Sounds go everywhere by default; the microphone goes only where it is
    /// deliberately sent.
    /// </summary>
    public bool ReceivesMic { get; set; }
}
