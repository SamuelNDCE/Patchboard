// System.IO is imported explicitly because UseWPF strips it out of the implicit usings.
// Microsoft.NET.Sdk.WindowsDesktop.WPF.props does "<Using Remove="System.IO" />" so that
// System.IO.Path cannot collide with System.Windows.Shapes.Path, which means Path, File
// and Directory are all unresolved in a WPF project without this line.
using System.IO;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>
/// The outcome of one import run.
/// </summary>
/// <param name="Imported">Buttons actually produced.</param>
/// <param name="Skipped">
/// Source entries that were looked at and did not become a button, for any reason:
/// a missing file, a duplicate, a blank grid slot, or an unreadable row.
/// </param>
/// <param name="Sounds">The buttons, already ordered and numbered.</param>
/// <param name="Warnings">
/// Everything the caller should see, including the shape of the source data. Safe to
/// show verbatim; nothing here is an exception the UI needs to interpret.
/// </param>
public sealed record ImportResult(
    int Imported,
    int Skipped,
    IReadOnlyList<SoundButton> Sounds,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds buttons from a folder of audio files.
///
/// This used to also read Resanance's own LiteDB database, which is how the original 206
/// sounds arrived. Samuel asked for that removed: the import was a one off, it is done,
/// and what it dragged in was a folder of download artefacts including three hour long
/// music rips. Dropping it also drops the LiteDB dependency, which is dead weight in an
/// app that stores everything in one JSON file.
///
/// The folder scan stays, because it is how a library gets added in bulk and because the
/// label cleanup that runs afterwards is worth keeping.
/// </summary>
public sealed class FolderImporter
{
    /// <summary>
    /// What <see cref="CachedSound"/> can decode through NAudio's AudioFileReader, which
    /// is the real limit on what is worth importing.
    /// </summary>
    private static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma"];




    /// <summary>
    /// Scan a folder for audio files and build buttons from them. Recurses one level, so
    /// the folder itself and its immediate subfolders, never deeper.
    ///
    /// Files that are not audio are not candidates and are not counted as skipped.
    /// Never throws; an unreadable folder comes back as a warning.
    /// </summary>
    public ImportResult ImportFromFolder(string folderPath)
    {
        var collector = new Collector();

        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                collector.WarnAlways($"Folder not found: {folderPath}");
                return collector.Build();
            }

            var root = Path.GetFullPath(folderPath);

            // One level only. A sound library is normally a folder of clips with a few
            // themed subfolders; recursing without limit would drag an entire music
            // collection onto the board the first time someone points this at Downloads.
            var directories = new List<string> { root };
            try
            {
                directories.AddRange(Directory
                    .EnumerateDirectories(root)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                collector.WarnAlways($"Could not list the subfolders of {root}: {ex.Message}");
            }

            foreach (var directory in directories)
            {
                List<string> files;
                try
                {
                    files = Directory
                        .EnumerateFiles(directory)
                        .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    // A single denied subfolder is normal on Windows. Keep going.
                    collector.Warn($"Could not read {directory}: {ex.Message}");
                    continue;
                }

                foreach (var file in files) collector.Add(file, label: null, volume: 1f);
            }
        }
        catch (Exception ex)
        {
            collector.WarnAlways($"Could not scan '{folderPath}': {ex.Message}");
        }

        return collector.Build();
    }

    /// <summary>
    /// Accumulates candidate buttons under the rules both import paths share: absolute
    /// paths, no duplicates, nothing that is not on disk, and a bounded warning list.
    /// </summary>
    private sealed class Collector
    {
        /// <summary>
        /// Enough named warnings to see the pattern, few enough that a wrecked library
        /// cannot produce a list nobody will read.
        /// </summary>
        private const int MaxNamedWarnings = 50;

        private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SoundButton> _buttons = [];
        private readonly List<string> _warnings = [];

        private int _skipped;
        private int _missing;
        private int _relocated;
        private int _duplicates;
        private int _emptySlots;
        private int _boosted;
        private int _named;
        private int _suppressed;

        /// <summary>A warning about one entry. Dropped once the list is full.</summary>
        public void Warn(string message)
        {
            if (_named >= MaxNamedWarnings)
            {
                _suppressed++;
                return;
            }

            _named++;
            _warnings.Add(message);
        }

        /// <summary>A warning about the import as a whole. Always kept.</summary>
        public void WarnAlways(string message) => _warnings.Add(message);

        /// <summary>A source row that never held a file: a blank grid cell or a control action.</summary>
        public void SkipEmptySlot()
        {
            _skipped++;
            _emptySlots++;
        }

        public void NoteBoost() => _boosted++;

        /// <summary>One source row, held until Build can see where the library really is.</summary>
        private sealed record Pending(string FullPath, string? Label, float Volume);

        private readonly List<Pending> _pending = [];

        public void Add(string rawPath, string? label, float volume)
        {
            string full;
            try
            {
                full = Path.GetFullPath(rawPath.Trim());
            }
            catch (Exception ex)
            {
                _skipped++;
                Warn($"Could not make sense of the path '{rawPath}': {ex.Message}");
                return;
            }

            if (!_seenPaths.Add(full))
            {
                _skipped++;
                _duplicates++;
                return;
            }

            // Existence is NOT decided here. Whether a path is dead or merely stale can
            // only be known once every row has been seen, because a moved library shows up
            // as most paths failing and a few succeeding in the folder they moved to.
            _pending.Add(new Pending(full, label, volume));
        }

        /// <summary>
        /// Folders that actually contain at least one of the referenced files. These are
        /// treated as the library's real home when relocating stale paths.
        /// </summary>
        private List<string> FoundDirectories() =>
            _pending
                .Where(p => File.Exists(p.FullPath))
                .Select(p => Path.GetDirectoryName(p.FullPath))
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public ImportResult Build()
        {
            // A library that has been moved wholesale leaves every path pointing at a
            // folder that no longer exists. Rather than dropping the lot, look for each
            // missing file by name in the folders where the surviving files turned up.
            // Samuel's own library moved from Downloads\OldImportedBoard to Sounds\ImportedBoard,
            // which without this rescues 4 sounds out of 48.
            var searchDirectories = FoundDirectories();
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _pending)
            {
                var path = entry.FullPath;

                if (!File.Exists(path))
                {
                    var name = Path.GetFileName(path);
                    var relocatedTo = searchDirectories
                        .Select(directory => Path.Combine(directory, name))
                        .FirstOrDefault(File.Exists);

                    if (relocatedTo is null)
                    {
                        // Importing it anyway would put a button on the board that fails the
                        // moment it is pressed, which is worse than not having it.
                        _skipped++;
                        _missing++;
                        Warn($"File is gone, not imported: {path}");
                        continue;
                    }

                    path = relocatedTo;
                    _relocated++;
                }

                // Relocation can land two different stale paths on the same real file.
                if (!resolved.Add(path))
                {
                    _skipped++;
                    _duplicates++;
                    continue;
                }

                _buttons.Add(new SoundButton
                {
                    Name = string.IsNullOrWhiteSpace(entry.Label)
                        ? Path.GetFileNameWithoutExtension(path)
                        : entry.Label.Trim(),
                    FilePath = path,
                    Volume = Math.Clamp(entry.Volume, 0f, 1f),

                    // Hotkey is deliberately left unset. Resanance stores its bindings as its
                    // own key names, not Win32 virtual-key codes, and a wrong mapping would
                    // silently bind real global hotkeys to the wrong sounds. An unbound button
                    // is obvious and takes seconds to fix; a mis-bound one is neither.
                });
            }

            for (var i = 0; i < _buttons.Count; i++) _buttons[i].Order = i;

            if (_relocated > 0)
                WarnAlways($"{Entries(_relocated)} pointed at the old location of a sound that has since moved, and {Were(_relocated)} found in {(searchDirectories.Count == 1 ? searchDirectories[0] : "the folders your other sounds are in")}.");

            if (_emptySlots > 0)
                WarnAlways($"{Entries(_emptySlots)} held no audio file (blank grid slots or Resanance's own control actions) and {Were(_emptySlots)} ignored.");

            if (_duplicates > 0)
                WarnAlways($"{Entries(_duplicates)} pointed at a file that was already imported and {Were(_duplicates)} merged into the existing button.");

            if (_missing > 0)
                WarnAlways($"{Entries(_missing)} pointed at files that are no longer on disk. If the sound library was moved, scan the folder it moved to instead.");

            if (_boosted > 0)
                WarnAlways($"{Entries(_boosted)} were set above 100% in Resanance and came in at 100%.");

            if (_suppressed > 0)
                WarnAlways($"{_suppressed} further warnings were not listed.");

            return new ImportResult(_buttons.Count, _skipped, _buttons, _warnings);
        }

        // These lists are read by a dyslexic user, so "1 entries were" is not acceptable
        // output. See the accessibility note in the global rules.
        private static string Entries(int count) => count == 1 ? "1 entry" : $"{count} entries";

        private static string Were(int count) => count == 1 ? "was" : "were";
    }
}
