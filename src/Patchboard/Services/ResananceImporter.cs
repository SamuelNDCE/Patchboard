// System.IO is imported explicitly because UseWPF strips it out of the implicit usings.
// Microsoft.NET.Sdk.WindowsDesktop.WPF.props does "<Using Remove="System.IO" />" so that
// System.IO.Path cannot collide with System.Windows.Shapes.Path, which means Path, File
// and Directory are all unresolved in a WPF project without this line.
using System.IO;
using LiteDB;
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
/// Reads the sound library out of Resanance, the app Patchboard replaces.
///
/// Two ways in. <see cref="ImportFromDatabase"/> reads Resanance's own LiteDB file, which
/// keeps the labels and volumes he set. <see cref="ImportFromFolder"/> scans a directory,
/// which is the fallback when the database is gone and also the answer when the library
/// has been moved on disk since Resanance last saw it: the stored paths are then all dead
/// and only a folder scan finds the files.
///
/// Nothing in this class writes to Resanance's data directory. See
/// <see cref="CopyDatabaseAside"/> for why that needs more than opening the file read only.
/// </summary>
public sealed class ResananceImporter
{
    /// <summary>
    /// What <see cref="CachedSound"/> can decode through NAudio's AudioFileReader, which
    /// is the real limit on what is worth importing.
    /// </summary>
    private static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma"];

    // Field names are probed rather than assumed. The live database uses "shortname",
    // "vol" and "index", but Resanance has shipped several versions and this costs
    // nothing next to failing the whole import on a renamed column.
    private static readonly string[] NameFields = ["shortname", "name", "label", "title", "displayname"];
    private static readonly string[] VolumeFields = ["vol", "volume", "gain"];
    private static readonly string[] OrderFields = ["index", "order", "position", "slot"];

    /// <summary>LiteDB v5's fixed page size, used only to sanity check the file.</summary>
    private const int LiteDbPageSize = 8192;

    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Resanance", "data", "Resanance.db");

    /// <summary>True when a Resanance database is present on this machine.</summary>
    public static bool IsAvailable => File.Exists(DatabasePath);

    /// <summary>
    /// Read the Resanance library and return buttons for it.
    ///
    /// Never throws. Every failure, from a missing file to a database written by a
    /// version we cannot open, comes back as warnings on an otherwise empty result, so a
    /// broken import is a message in the UI rather than a crash on startup.
    /// </summary>
    public ImportResult ImportFromDatabase()
    {
        var collector = new Collector();
        string? workingDirectory = null;

        try
        {
            if (!File.Exists(DatabasePath))
            {
                collector.WarnAlways($"No Resanance database found at {DatabasePath}.");
                return collector.Build();
            }

            var length = new FileInfo(DatabasePath).Length;
            if (length == 0)
            {
                collector.WarnAlways("The Resanance database is zero bytes, so there is nothing to import.");
                return collector.Build();
            }

            if (length % LiteDbPageSize != 0)
            {
                collector.WarnAlways(
                    "The Resanance database does not end on a page boundary, so it may have been " +
                    "cut short. Reading it anyway; anything missing was already missing.");
            }

            workingDirectory = CopyDatabaseAside();

            var connection = new ConnectionString
            {
                Filename = Path.Combine(workingDirectory, Path.GetFileName(DatabasePath)),
                ReadOnly = true,
                Connection = ConnectionType.Direct,
            };

            using var database = new LiteDatabase(connection);

            var collections = database.GetCollectionNames().ToList();

            // Reported rather than inferred. If a future Resanance renames or splits these,
            // this line is what tells the next person what they are actually looking at.
            collector.WarnAlways(collections.Count == 0
                ? "The Resanance database contains no collections."
                : $"Resanance collections found: {string.Join(", ", collections)}.");

            foreach (var name in collections)
            {
                try
                {
                    ReadCollection(database, name, collector);
                }
                catch (Exception ex)
                {
                    // One unreadable collection must not lose the entries in the others.
                    collector.WarnAlways($"Collection '{name}' could not be read: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            collector.WarnAlways($"Could not read the Resanance database: {ex.Message}");
        }
        finally
        {
            if (workingDirectory is not null) TryDeleteDirectory(workingDirectory);
        }

        return collector.Build();
    }

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
    /// Pull every playable entry out of one collection, in Resanance's own grid order.
    /// </summary>
    private static void ReadCollection(LiteDatabase database, string name, Collector collector)
    {
        var rows = new List<(int Order, string Path, string? Label, float Volume)>();

        foreach (var document in database.GetCollection(name).FindAll())
        {
            try
            {
                // The collection name is not trusted to say what is in it. Resanance keeps
                // its control actions ("Stop Playback") in a collection alongside the real
                // sounds and marks unused grid cells with the literal string "empty", so a
                // row counts as playable only when it actually holds a path to an audio
                // file. Blank slots and control rows then fall out on their own.
                var path = FindAudioPath(document);
                if (path is null)
                {
                    collector.SkipEmptySlot();
                    continue;
                }

                var label = ReadString(document, NameFields);

                // A fork that copies the path into its name field would otherwise put a
                // full absolute path on the face of the button.
                if (label is not null && LooksLikeAudioPath(label)) label = null;

                var (volume, wasBoosted) = ReadVolume(document);
                if (wasBoosted) collector.NoteBoost();

                rows.Add((ReadInt(document, OrderFields) ?? rows.Count, path, label, volume));
            }
            catch (Exception ex)
            {
                // One malformed document must not cost the rest of the library.
                collector.SkipEmptySlot();
                collector.Warn($"Skipped an unreadable row in '{name}': {ex.Message}");
            }
        }

        // Resanance's own grid index decides the running order, so the imported board comes
        // out in roughly the layout he already has muscle memory for.
        foreach (var row in rows.OrderBy(r => r.Order))
            collector.Add(row.Path, row.Label, row.Volume);
    }

    /// <summary>
    /// Find the one value in a document that is a rooted path to an audio file.
    /// Returns null when the row holds no playable file.
    /// </summary>
    private static string? FindAudioPath(BsonDocument document)
    {
        foreach (var pair in document)
        {
            if (!pair.Value.IsString) continue;

            var value = pair.Value.AsString?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (LooksLikeAudioPath(value)) return value;
        }

        return null;
    }

    private static bool LooksLikeAudioPath(string value)
    {
        try
        {
            if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (!Path.IsPathRooted(value)) return false;

            return AudioExtensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A stored value can be any string at all. Anything unparseable is not a path.
            return false;
        }
    }

    /// <summary>
    /// Read a percentage volume and bring it into our own 0.0 to 1.0 range.
    /// </summary>
    private static (float Volume, bool WasBoosted) ReadVolume(BsonDocument document)
    {
        var raw = ReadDouble(document, VolumeFields);
        if (raw is null) return (1f, false);

        // Resanance lets a sound sit above 100%. Our per-sound gain is a multiplier that
        // the engine clamps to 1.0 anyway, so a boost is folded back to unity at import
        // rather than persisted as a number that can never take effect.
        return ((float)Math.Clamp(raw.Value / 100.0, 0.0, 1.0), raw.Value > 100.0);
    }

    private static string? ReadString(BsonDocument document, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!TryGetField(document, candidate, out var value) || !value.IsString) continue;

            var text = value.AsString?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        return null;
    }

    private static int? ReadInt(BsonDocument document, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryGetField(document, candidate, out var value) && value.IsNumber) return value.AsInt32;
        }

        return null;
    }

    private static double? ReadDouble(BsonDocument document, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryGetField(document, candidate, out var value) && value.IsNumber) return value.AsDouble;
        }

        return null;
    }

    /// <summary>
    /// Case-insensitive field lookup. BsonDocument's own indexer is case sensitive, and a
    /// field spelled "Vol" instead of "vol" is not worth losing a volume over.
    /// </summary>
    private static bool TryGetField(BsonDocument document, string key, out BsonValue value)
    {
        foreach (var pair in document)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;

            value = pair.Value;
            return true;
        }

        value = BsonValue.Null;
        return false;
    }

    /// <summary>
    /// Copy the database to a scratch directory and return that directory.
    ///
    /// LiteDB is never pointed at the real file, because opening a connection read only is
    /// not enough to guarantee nothing is written beside it. Two paths inside LiteDB 5
    /// ignore that flag: FileStreamFactory.GetLength opens its own write handle to trim a
    /// half written final page, and the sort spill file is created as "&lt;name&gt;-tmp.db"
    /// next to the datafile. Either would put our writes inside %APPDATA%\Resanance, which
    /// PROJECT.md forbids. Reading a copy makes that impossible instead of unlikely, and it
    /// costs a 64KB file copy.
    /// </summary>
    private static string CopyDatabaseAside()
    {
        var directory = Directory.CreateTempSubdirectory("patchboard-resanance-").FullName;

        CopyShared(DatabasePath, Path.Combine(directory, Path.GetFileName(DatabasePath)));

        // Anything Resanance wrote since its last checkpoint is still in the write ahead
        // log, so the newest sounds only appear if the log comes along under the name
        // LiteDB derives from the datafile.
        var log = LogFileFor(DatabasePath);
        if (File.Exists(log)) CopyShared(log, Path.Combine(directory, Path.GetFileName(log)));

        return directory;
    }

    /// <summary>
    /// Copy a file that another process may currently have open.
    /// </summary>
    private static void CopyShared(string source, string destination)
    {
        // FileShare.ReadWrite because Resanance may be running right now and holds its
        // datafile open. Asking for stricter sharing terms would fail exactly when he is
        // most likely to be importing, which is with the old app still in front of him.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>Mirrors LiteDB's own log file naming, "name-log.ext" beside the datafile.</summary>
    private static string LogFileFor(string path) => Path.Combine(
        Path.GetDirectoryName(path) ?? string.Empty,
        Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // A leftover copy under TEMP is not worth failing a successful import over.
        }
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
