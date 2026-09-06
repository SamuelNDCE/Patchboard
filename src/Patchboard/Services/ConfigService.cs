using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>
/// Reads and writes <see cref="AppConfig"/> as a single JSON document under
/// <c>%APPDATA%\Patchboard</c>.
///
/// Two properties matter more than anything else here. Loading is total: the app must
/// open even when the file on disk is nonsense, because a soundboard that refuses to
/// start is worse than one that starts empty. Saving is atomic: the app writes on every
/// change, so a crash during a write would otherwise be able to leave a half finished
/// document where the whole library used to be.
/// </summary>
public sealed class ConfigService
{
    private const int MaxGridSide = 20;
    private const int MinLatencyMs = 20;
    private const int MaxLatencyMs = 500;

    /// <summary>Longest lead in worth allowing. Past a couple of seconds it reads as broken.</summary>
    private const int MaxDelayMs = 5000;

    /// <summary>Give up looking for a free quarantine slot after this many tries.</summary>
    private const int MaxQuarantineSlots = 999;

    /// <summary>
    /// Tolerant on read, tidy on write. The file is meant to be hand editable, so
    /// comments, trailing commas and casual property casing are all accepted, and enums
    /// are written as names because "Control, Shift" is editable and 6 is not.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },

        // Without this the whole file refuses to write the moment any double is NaN, and
        // AppConfig.WindowLeft and WindowTop are NaN by design until a window position has
        // actually been recorded. So on a machine that has never saved one, which is every
        // machine on first run, every save threw and nothing persisted: sounds, devices and
        // volumes were all silently lost until the window happened to be closed normally,
        // and lost outright if it was closed while minimised. NaN survives a round trip
        // now, which is what the double.IsNaN checks in the window restore expect.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Serialises concurrent saves so two writers cannot interleave a swap.</summary>
    private readonly Lock _saveGate = new();

    // Declaration order is load bearing: static initialisers run top to bottom, so
    // ConfigDirectory has to be assigned before the two paths built from it.
    //
    // PATCHBOARD_CONFIG_DIR overrides the real %APPDATA%\Patchboard entirely, for a
    // fully isolated run (demo screenshots, a portable copy) that can never read or
    // write the real user's library. Environment.SpecialFolder.ApplicationData is
    // resolved from the registry on Windows, not from the APPDATA variable, so there is
    // no other way to redirect it per process without touching the real folder.
    public static string ConfigDirectory { get; } = Environment.GetEnvironmentVariable("PATCHBOARD_CONFIG_DIR")
        is { Length: > 0 } overrideDir
        ? overrideDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Patchboard");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Where imported or user added sounds are copied to. The user's original files are
    /// never moved or deleted, so a sound added from the Downloads folder still plays
    /// after that folder is cleared out.
    /// </summary>
    public static string SoundsDirectory { get; } = Path.Combine(ConfigDirectory, "sounds");

    /// <summary>
    /// Read the saved config. Never throws. A missing, empty, unreadable or corrupt file
    /// yields a fresh default, and a corrupt one is renamed aside first so a library is
    /// never silently destroyed by a parse failure.
    /// </summary>
    public AppConfig Load()
    {
        try
        {
            return Validate(ReadFromDisk());
        }
        catch (Exception)
        {
            return Validate(new AppConfig());
        }
    }

    private static AppConfig ReadFromDisk()
    {
        EnsureDirectories();

        if (!File.Exists(ConfigPath)) return new AppConfig();

        string text;
        try
        {
            text = File.ReadAllText(ConfigPath);
        }
        catch (Exception)
        {
            // A locked or permission denied file is not a corrupt one. Leaving it exactly
            // as it is means a transient lock cannot cost the user their library.
            return new AppConfig();
        }

        if (string.IsNullOrWhiteSpace(text)) return new AppConfig();

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, DocumentOptions) as JsonObject;
        }
        catch (Exception)
        {
            // Not only JsonException: a duplicated property name surfaces as an
            // ArgumentException out of the node builder rather than as a parse error.
            root = null;
        }

        if (root is null)
        {
            Quarantine();
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(root, SerializerOptions) ?? new AppConfig();
        }
        catch (Exception)
        {
            // One unreadable sound must not cost the other forty, so the document is
            // rebuilt from the parts that still deserialize. That necessarily discards
            // whatever could not be read, which is why the original is kept aside.
            var salvaged = Salvage(root);
            Quarantine();
            return salvaged;
        }
    }

    /// <summary>
    /// Write the config, replacing the previous one in a single step.
    ///
    /// The new document goes to a uniquely named temporary file in the same directory, is
    /// flushed to the device, and only then moved over the real path. A move within one
    /// directory is a rename, so a reader sees either the whole old file or the whole new
    /// one and never a partial write.
    ///
    /// Throws on a genuine IO or permission failure rather than swallowing it, because a
    /// save that silently did nothing is data loss the user finds out about much later.
    /// Clamps the config in place first, so what is on disk matches what is in memory.
    /// </summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureDirectories();
        Validate(config);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, SerializerOptions);
        var temporaryPath = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";

        lock (_saveGate)
        {
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);

                    // Without the flush to the device the rename can commit while the new
                    // contents are still only in the OS cache, which on a power cut gives
                    // a config.json full of zero bytes.
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, ConfigPath, overwrite: true);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                    // Best effort. A stray .tmp is harmless next to a failed save.
                }

                throw;
            }
        }
    }

    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(SoundsDirectory);
    }

    /// <summary>
    /// Rebuild a config from the pieces of <paramref name="root"/> that still parse.
    ///
    /// Each property, and each element of each array, is probed on its own against a
    /// throwaway document. Anything that fails is dropped and the model's own default
    /// takes over. Probing rather than mapping property by property is deliberate: a
    /// field added to <see cref="AppConfig"/> later is handled here without an edit.
    /// </summary>
    private static AppConfig Salvage(JsonObject root)
    {
        var kept = new JsonObject();

        foreach (var (name, value) in root)
        {
            var candidate = value?.DeepClone();
            if (candidate is null) continue;

            if (candidate is JsonArray array) candidate = KeepReadableElements(name, array);

            if (Deserializes(name, candidate)) kept[name] = candidate;
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(kept, SerializerOptions) ?? new AppConfig();
        }
        catch (Exception)
        {
            return new AppConfig();
        }
    }

    private static JsonArray KeepReadableElements(string propertyName, JsonArray array)
    {
        var kept = new JsonArray();

        foreach (var element in array)
        {
            var candidate = element?.DeepClone();
            if (candidate is null) continue;

            if (Deserializes(propertyName, new JsonArray(candidate.DeepClone()))) kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>True when one property, read in isolation, still produces a value.</summary>
    private static bool Deserializes(string propertyName, JsonNode candidate)
    {
        try
        {
            // The clone matters. A node can only have one parent, and the caller still
            // needs this one unattached so it can be kept.
            _ = JsonSerializer.Deserialize<AppConfig>(
                new JsonObject { [propertyName] = candidate.DeepClone() }, SerializerOptions);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Move the file on disk aside under a numbered name. Renaming rather than copying is
    /// what stops a broken file being re-read and re-quarantined on every launch.
    /// </summary>
    private static void Quarantine()
    {
        try
        {
            for (var slot = 1; slot <= MaxQuarantineSlots; slot++)
            {
                var candidate = Path.Combine(ConfigDirectory, $"config.corrupt-{slot}.json");
                if (File.Exists(candidate)) continue;

                File.Move(ConfigPath, candidate);
                return;
            }

            File.Move(ConfigPath, Path.Combine(
                ConfigDirectory, $"config.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        }
        catch (Exception)
        {
            // The caller still gets a usable config, which is the part that has to work.
        }
    }

    /// <summary>
    /// Repair rather than reject. Every value is pulled back into a range the audio engine
    /// and the grid can actually handle, and entries that could never work are dropped, so
    /// a hand edited or half salvaged file still produces a running app.
    /// </summary>
    private static AppConfig Validate(AppConfig config)
    {
        // Explicit JSON nulls land in these properties despite their declared types, so
        // the model's own defaults are no guarantee once a file has been read.
        config.Sounds = OrNew(config.Sounds);
        config.OutputDevices = OrNew(config.OutputDevices);
        config.InputDevices = OrNew(config.InputDevices);
        config.StopAllHotkey = OrNew(config.StopAllHotkey);

        config.GridColumns = Math.Clamp(config.GridColumns, 1, MaxGridSide);
        config.GridRows = Math.Clamp(config.GridRows, 1, MaxGridSide);
        config.LatencyMs = Math.Clamp(config.LatencyMs, MinLatencyMs, MaxLatencyMs);
        config.MasterVolume = Clamp01(config.MasterVolume);
        config.DefaultDelayMs = Math.Clamp(config.DefaultDelayMs, 0, MaxDelayMs);

        // Window size has to be a real number. Left and Top deliberately may not be:
        // NaN there means "never positioned, let Windows choose", and the restore checks
        // for exactly that. Size has no such sentinel, so a non-finite one is repaired.
        config.WindowWidth = FiniteOr(config.WindowWidth, 1240, 200, 10000);
        config.WindowHeight = FiniteOr(config.WindowHeight, 780, 200, 10000);

        // An infinite coordinate is not the "unpositioned" sentinel and would place the
        // window nowhere, so it is folded back into one that is.
        if (double.IsInfinity(config.WindowLeft)) config.WindowLeft = double.NaN;
        if (double.IsInfinity(config.WindowTop)) config.WindowTop = double.NaN;

        // Filtered in place rather than replaced. Save validates too, and the view model
        // adds and removes through these same list instances, so handing it a new list on
        // every save would leave anything holding the old one silently editing a corpse.
        //
        // A button with no file can never be played and cannot be repaired from here,
        // because nothing is left to say which sound it was meant to be.
        config.Sounds.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.FilePath));

        foreach (var sound in config.Sounds)
        {
            if (string.IsNullOrWhiteSpace(sound.Id)) sound.Id = Guid.NewGuid().ToString("N");

            sound.Name = OrEmpty(sound.Name);
            sound.FilePath = sound.FilePath.Trim();
            sound.Hotkey = OrNew(sound.Hotkey);
            // Sounds may be boosted above unity, unlike device and master gain.
            sound.Volume = Math.Clamp(sound.Volume, 0f, SoundButton.MaxVolume);

            // Anything below the sentinel collapses to it, so a hand edited -5 means
            // "follow the default" rather than a negative wait.
            sound.DelayMs = sound.DelayMs < 0
                ? SoundButton.UseDefaultDelay
                : Math.Min(sound.DelayMs, MaxDelayMs);
        }

        ValidateDevices(config.OutputDevices);
        ValidateDevices(config.InputDevices);

        return config;
    }

    private static void ValidateDevices(List<AudioDeviceRef> devices)
    {
        // DeviceService matches on endpoint id and falls back to the friendly name. With
        // neither, the entry can never resolve and would sit in the device list as a
        // nameless row that permanently reports itself as missing.
        devices.RemoveAll(d => d is null
            || (string.IsNullOrWhiteSpace(d.Id) && string.IsNullOrWhiteSpace(d.FriendlyName)));

        foreach (var device in devices)
        {
            device.Id = OrEmpty(device.Id);
            device.FriendlyName = OrEmpty(device.FriendlyName);
            device.Volume = Clamp01(device.Volume);
        }
    }

    /// <summary>
    /// Clamp a gain to 0..1, treating a non-finite value as full volume.
    ///
    /// Math.Clamp passes NaN straight through, and System.Text.Json refuses to write a
    /// non-finite number at all, so a NaN that survived to Save would throw out of the
    /// serializer and the config would then never be written again.
    /// </summary>
    private static float Clamp01(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;

    /// <summary>Clamp a dimension, falling back to a default when it is NaN or infinite.</summary>
    private static double FiniteOr(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string OrEmpty(string? value) => value ?? "";

    private static T OrNew<T>(T? value) where T : class, new() => value ?? new T();
}
