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
}
