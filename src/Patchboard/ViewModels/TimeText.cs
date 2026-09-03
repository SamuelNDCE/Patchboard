namespace Patchboard.ViewModels;

/// <summary>
/// Turning a number of seconds into something a person reads without converting it.
///
/// Two forms, for two jobs. A length is prose, because it is read once and understood:
/// "49 minutes 10 seconds". A position is a clock, because it is read at a glance while
/// something moves: "49:10". Using the prose form for a moving readout would make it
/// change width constantly, and using the clock form for a length reads as a duration you
/// have to decode.
///
/// This exists because the trim panel showed "2950.0s long" for a 49 minute clip, which is
/// technically the length and tells you nothing.
///
/// Named TimeText rather than the obvious Duration, because System.Windows.Duration exists
/// and a WPF file that imports both stops compiling on a name it never knew was contested.
/// </summary>
public static class TimeText
{
    /// <summary>
    /// A length in words: "15 seconds", "1 minute 30 seconds", "49 minutes 10 seconds".
    ///
    /// Sub-minute lengths keep one decimal, because the difference between a 0.4 second
    /// clip and a 0.9 second one is the difference between a tick and a thump, and a
    /// soundboard is full of both. Once it is past a minute the decimal is noise.
    /// </summary>
    public static string Words(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0 seconds";

        if (seconds < 60)
        {
            // "1 second", not "1 seconds". Cheap to get right and jarring to get wrong.
            var rounded = Math.Round(seconds, 1);
            return Math.Abs(rounded - 1) < 0.05 ? "1 second" : $"{rounded:0.#} seconds";
        }

        var total = (int)Math.Round(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;

        var parts = new List<string>(3);
        if (hours > 0) parts.Add(Plural(hours, "hour"));
        if (minutes > 0) parts.Add(Plural(minutes, "minute"));

        // A trailing "0 seconds" is noise, but "2 minutes" alone is not obviously exact,
        // so the seconds are dropped only when they really are zero.
        if (secs > 0) parts.Add(Plural(secs, "second"));

        return string.Join(" ", parts);
    }

    /// <summary>
    /// A position on a clock: "0:15", "1:30", "49:10", "1:02:03".
    /// Fixed shape, so a readout that updates does not jitter as the digits change.
    /// </summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private static string Plural(int value, string noun) => $"{value} {noun}{(value == 1 ? "" : "s")}";
}
