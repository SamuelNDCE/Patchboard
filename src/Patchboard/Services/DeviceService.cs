using NAudio.CoreAudioApi;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>A snapshot of one Windows audio endpoint, safe to hold on the UI thread.</summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    bool IsDefault,
    bool IsVirtual);

/// <summary>
/// Enumerates Windows audio endpoints and resolves saved references back to live devices.
///
/// This class never changes anything about the system's audio configuration. It does not
/// set default devices and does not touch endpoint volume. It reads, and it hands back
/// devices for our own output streams. See the hard rules in PROJECT.md.
/// </summary>
public sealed class DeviceService : IDisposable
{
    /// <summary>
    /// Endpoints whose name marks them as a virtual cable rather than real hardware.
    /// These are the ones that carry sound into Discord or a game as a "microphone",
    /// so the UI flags them to save the user guessing which entry to tick.
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "CABLE", "Voicemeeter", "VB-Audio", "Virtual", "Steam Streaming", "NVIDIA Virtual",
    ];

    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioDeviceInfo> ListOutputs() => List(DataFlow.Render);

    public IReadOnlyList<AudioDeviceInfo> ListInputs() => List(DataFlow.Capture);

    private IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        string? defaultId = null;
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                using (var def = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia))
                    defaultId = def.ID;
        }
        catch (Exception)
        {
            // A machine with no default endpoint for this direction is legitimate.
            // Carry on and mark nothing as default rather than failing the whole list.
        }

        var results = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                var name = device.FriendlyName;
                results.Add(new AudioDeviceInfo(
                    device.ID,
                    name,
                    device.ID == defaultId,
                    VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))));
            }
            catch (Exception)
            {
                // A device can vanish between enumeration and property read. Skip it.
            }
            finally
            {
                device.Dispose();
            }
        }

        // Real hardware first, virtual cables after, each alphabetical. Puts the things
        // you pick by ear at the top and the plumbing in a predictable block below.
        return results
            .OrderBy(d => d.IsVirtual)
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolve a saved reference to a live device.
    ///
    /// Matches on endpoint id first. Falls back to the friendly name only if the id is
    /// gone, which covers a driver reinstall assigning a new id to the same speakers.
    /// Returns null when the device is genuinely absent, and the caller shows it greyed
    /// out rather than silently dropping the user's routing.
    /// </summary>
    public MMDevice? Resolve(AudioDeviceRef reference, DataFlow flow)
    {
        try
        {
            var byId = _enumerator.GetDevice(reference.Id);
            if (byId is { State: DeviceState.Active }) return byId;
            byId?.Dispose();
        }
        catch (Exception)
        {
            // GetDevice throws for an unknown id. Fall through to the name match.
        }

        if (string.IsNullOrWhiteSpace(reference.FriendlyName)) return null;

        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (string.Equals(device.FriendlyName, reference.FriendlyName, StringComparison.OrdinalIgnoreCase))
                return device;
            device.Dispose();
        }

        return null;
    }

    public void Dispose() => _enumerator.Dispose();
}
