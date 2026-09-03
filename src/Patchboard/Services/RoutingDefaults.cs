namespace Patchboard.Services;

/// <summary>
/// Which outputs a soundboard should use when nobody has chosen yet.
///
/// Pulled out of the view model so it can be tested. The wiring around it needs a dispatcher
/// and opens real WASAPI devices, which makes it awkward and unsafe to exercise; the policy
/// is pure and is the part that was actually wrong. It stayed untested long enough that a
/// first launch ended up selecting speakers and no cable, and the only way to check it was to
/// move the real config aside and look, which turned out to observe the user's own edits
/// rather than the code's output.
/// </summary>
public static class RoutingDefaults
{
    /// <summary>
    /// Endpoint name prefixes that reach a voice chat, best first.
    ///
    /// VoiceMeeter's virtual input comes before a bare VB-Cable, and that order is the whole
    /// point rather than a preference. Both are usually installed together, but only one of
    /// them is carrying the user's microphone; the other is a cable with nothing attached to
    /// its far end. Offering CABLE Input unconditionally once sent this user to a dead end,
    /// because his VoiceMeeter reads his mic and routes it to B1 while nothing on the machine
    /// reads CABLE Output at all.
    /// </summary>
    private static readonly string[] CablePreference =
    [
        "Voicemeeter Input",
        "Voicemeeter AUX Input",
        "CABLE Input",
    ];

    /// <summary>
    /// The endpoint most likely to actually reach Discord or a game as a microphone, or null
    /// when the machine has no virtual cable installed at all.
    /// </summary>
    public static T? PickCable<T>(IEnumerable<T> outputs, Func<T, string> name) where T : class
    {
        var list = outputs as IList<T> ?? outputs.ToList();

        foreach (var prefix in CablePreference)
        {
            var match = list.FirstOrDefault(o =>
                name(o).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>
    /// The endpoint the user themselves should hear, which is the Windows default when there
    /// is one and any other piece of real hardware otherwise.
    ///
    /// Never a virtual cable. Picking one would mean the user hears nothing, which is the
    /// failure this whole class exists to avoid, just pointed the other way.
    /// </summary>
    public static T? PickMonitor<T>(IEnumerable<T> outputs, Func<T, string> name, Func<T, bool> isDefault,
        Func<T, bool> isVirtual) where T : class
    {
        var list = outputs as IList<T> ?? outputs.ToList();

        return list.FirstOrDefault(o => isDefault(o) && !isVirtual(o))
               ?? list.FirstOrDefault(o => !isVirtual(o));
    }

    /// <summary>
    /// Both picks for a fresh install: something the user hears, and something everyone else
    /// hears. Either may be null on an unusual machine, and the caller reports that rather
    /// than pretending it is set up.
    /// </summary>
    public static (AudioDeviceInfo? Monitor, AudioDeviceInfo? Cable) Choose(IEnumerable<AudioDeviceInfo> outputs)
    {
        var list = outputs as IList<AudioDeviceInfo> ?? outputs.ToList();

        return (
            PickMonitor(list, o => o.FriendlyName, o => o.IsDefault, o => o.IsVirtual),
            PickCable(list, o => o.FriendlyName));
    }
}
