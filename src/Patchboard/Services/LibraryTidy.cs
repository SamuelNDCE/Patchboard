using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>
/// Cleaning up a library that came in by the hundred.
///
/// A bulk import is never tidy: it brings dead entries, several copies of the same clip
/// under different names, and labels that are really download artefacts. Doing that by
/// hand across 206 buttons is not reasonable, so it lives here, as plain functions over a
/// list rather than inside the view model, because every one of them is worth testing.
///
/// Nothing in this class deletes a file. It only ever removes buttons or edits labels;
/// the audio on disk is the user's and is left alone.
/// </summary>
public static partial class LibraryTidy
{
    // ---- Dead buttons -----------------------------------------------------------

    /// <summary>Buttons whose file is no longer on disk.</summary>
    public static List<SoundButton> FindDead(IEnumerable<SoundButton> sounds) =>
        [.. sounds.Where(s => !string.IsNullOrWhiteSpace(s.FilePath) && !File.Exists(s.FilePath))];

    // ---- Duplicates -------------------------------------------------------------

    /// <summary>
    /// Groups of buttons that point at byte identical audio, one group per distinct clip,
    /// each group ordered so the entry worth keeping comes first.
    ///
    /// Size is only the cheap first pass. Two unrelated clips can share a byte count, so
    /// every candidate group is confirmed by hashing the contents; his library has a pair
    /// of unrelated 78KB files that size alone would have called duplicates.
    ///
    /// The keeper is the one with a hand written name if there is one, and otherwise the
    /// one with the shortest name, which is reliably the copy without "(1)" on the end.
    /// </summary>
    public static List<List<SoundButton>> FindDuplicates(IEnumerable<SoundButton> sounds)
    {
        var bySize = new Dictionary<long, List<SoundButton>>();

        foreach (var sound in sounds)
        {
            long length;
            try
            {
                var info = new FileInfo(sound.FilePath);
                if (!info.Exists) continue;
                length = info.Length;
            }
            catch (Exception)
            {
                continue;
            }

            if (!bySize.TryGetValue(length, out var list)) bySize[length] = list = [];
            list.Add(sound);
        }

        var groups = new List<List<SoundButton>>();

        foreach (var candidates in bySize.Values.Where(v => v.Count > 1))
        {
            var byHash = new Dictionary<string, List<SoundButton>>(StringComparer.Ordinal);

            foreach (var sound in candidates)
            {
                var hash = HashOrNull(sound.FilePath);
                if (hash is null) continue;

                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                list.Add(sound);
            }

            foreach (var same in byHash.Values.Where(v => v.Count > 1))
                groups.Add([.. same.OrderBy(RemovalPriority).ThenBy(s => s.DisplayName.Length)]);
        }

        return groups;
    }

    /// <summary>A hand named button sorts first, so it is the copy that survives.</summary>
    private static int RemovalPriority(SoundButton sound) => IsUntouchedName(sound) ? 1 : 0;

    private static string? HashOrNull(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            // Unreadable is not the same as duplicate. Leave it alone.
            return null;
        }
    }

    // ---- Names ------------------------------------------------------------------

    // Trailing " (1)", " (12)" from a browser downloading the same file twice.
    [GeneratedRegex(@"\s*\(\d{1,3}\)$")] private static partial Regex CopySuffix();

    // Leading "Y2meta.app - ", "y2mate.com - ", and the rest of the ripper sites.
    [GeneratedRegex(@"^\s*[\w.]+\.(?:app|com|net|org|cc|io)\s*-\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RipperPrefix();

    // Trailing "_PjJg6Snw", "_7zPAD7C", "_WstdzdM": a download id, not a word.
    //
    // Seven or more characters, and either a digit or an uppercase letter somewhere after
    // the first character. Both conditions matter: the length alone would eat "_reversed",
    // and requiring a digit alone left "_WstdzdM" sitting on a tile. Real words do not
    // have capitals in the middle, so the internal-uppercase test is what separates an id
    // from "_final" or "_loud".
    [GeneratedRegex(@"_(?=[A-Za-z0-9]{7,}$)[A-Za-z0-9](?=[A-Za-z0-9]*(?:\d|[A-Z]))[A-Za-z0-9]+$")]
    private static partial Regex DownloadId();

    // A filename that is just a GUID carries no meaning to recover, and turning its
    // hyphens into spaces only makes it longer and no clearer. Left exactly as it is.
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidName();

    // Editor and converter leftovers.
    [GeneratedRegex(@"[\s_-]*(?:mp3cut|_small|_short|converted|audiotrimmer)$", RegexOptions.IgnoreCase)]
    private static partial Regex ToolSuffix();

    // "(128 kbps)", "(youtube)", "(official audio)" and friends.
    [GeneratedRegex(@"\s*\((?:\d+\s*kbps|youtube|official\s+\w+|lyrics|audio|hd|hq)\)", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseParens();

    // A leading track number: "05. ", "03 - ".
    [GeneratedRegex(@"^\s*\d{1,2}\s*[.\-]\s+")] private static partial Regex TrackNumber();

    [GeneratedRegex(@"\s{2,}")] private static partial Regex Runs();

    /// <summary>
    /// Turn a download artefact into something readable on a tile.
    /// Returns the input unchanged when there is nothing safe to remove.
    /// </summary>
    public static string CleanName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // Nothing readable to recover from a GUID, so do not try.
        if (GuidName().IsMatch(raw)) return raw;

        var name = raw;
        name = RipperPrefix().Replace(name, "");
        name = CopySuffix().Replace(name, "");
        name = NoiseParens().Replace(name, "");
        name = ToolSuffix().Replace(name, "");
        name = DownloadId().Replace(name, "");
        name = TrackNumber().Replace(name, "");

        // Separators only become spaces once the id-shaped pieces are gone, so an
        // underscore that was part of a download id never turns into a stray space.
        name = name.Replace('_', ' ').Replace('-', ' ');
        name = Runs().Replace(name, " ").Trim();

        // Never hand back nothing. A name made entirely of noise keeps its original.
        return name.Length == 0 ? raw : name;
    }

    /// <summary>
    /// True when the label is still just the file name, so the user has never renamed it
    /// and tidying it cannot destroy anything they chose.
    /// </summary>
    public static bool IsUntouchedName(SoundButton sound)
    {
        if (string.IsNullOrWhiteSpace(sound.FilePath)) return false;
        var stem = Path.GetFileNameWithoutExtension(sound.FilePath);
        return string.IsNullOrWhiteSpace(sound.Name) || sound.Name == stem;
    }

    /// <summary>
    /// The buttons whose label would change, with the label they would get.
    ///
    /// Only ever considers names the user has not touched. A hand written label is a
    /// decision, and a bulk tidy has no business overruling 43 of them.
    /// </summary>
    public static List<(SoundButton Sound, string NewName)> PlanNameTidy(IEnumerable<SoundButton> sounds)
    {
        var plan = new List<(SoundButton, string)>();

        foreach (var sound in sounds)
        {
            if (!IsUntouchedName(sound)) continue;

            var current = Path.GetFileNameWithoutExtension(sound.FilePath);
            var cleaned = CleanName(current);
            if (cleaned != current) plan.Add((sound, cleaned));
        }

        return plan;
    }
}
