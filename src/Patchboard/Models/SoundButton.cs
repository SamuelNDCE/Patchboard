namespace Patchboard.Models;

/// <summary>
/// What happens when a sound is triggered while it is already playing.
/// </summary>
public enum RetriggerMode
{
    /// <summary>Start another copy on top. Good for short stabs.</summary>
    Overlap = 0,

    /// <summary>Stop the running copy and start again from zero.</summary>
    Restart = 1,

    /// <summary>Second press stops it. Good for music beds.</summary>
    Toggle = 2,
}

/// <summary>
/// One bound sound. Serialised to config.json; nothing here holds audio data or
/// unmanaged handles, so it is safe to copy and rebind freely.
/// </summary>
public sealed class SoundButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Label shown on the button. Defaults to the file name without extension.</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path to the audio file. Machine specific, so config.json is never tracked.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Optional image shown as the button face.</summary>
    public string? ImagePath { get; set; }

    /// <summary>Optional custom button colour as "#RRGGBB". Null means the default surface.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Ceiling for per-sound gain. Above 1.0 is a genuine boost for quiet recordings.
    /// Pushed far enough it will clip, which is the honest consequence of asking for more
    /// level than the sample has, so the UI marks the region rather than preventing it.
    /// </summary>
    public const float MaxVolume = 2.0f;

    /// <summary>Per-sound gain, 0.0 to 2.0, multiplied with each device's volume and the master.</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>
    /// Toggle by default. Pressing a button that is already sounding stops it, which is
    /// what a soundboard user reaches for when a clip is running long. Overlap is still
    /// available per sound for short stabs you want to stack.
    /// </summary>
    public RetriggerMode Retrigger { get; set; } = RetriggerMode.Toggle;

    /// <summary>
    /// Where in the file to start, in milliseconds. 0 is the beginning.
    ///
    /// Trimming is what makes a long recording usable on a board at all: only the trimmed
    /// window is decoded, so the length limit applies to the piece you kept rather than to
    /// the file. Three of the imported buttons are hour long music rips that could never
    /// play; a start and an end turns them into the eight seconds anyone actually wanted.
    /// </summary>
    public int StartMs { get; set; }

    /// <summary>Where to stop, in milliseconds. 0 means play to the end of the file.</summary>
    public int EndMs { get; set; }

    /// <summary>True when only part of the file is used.</summary>
    public bool IsTrimmed => StartMs > 0 || EndMs > 0;

    /// <summary>
    /// Identity for the decoded-audio cache. Two buttons on the same file with different
    /// trims are different audio and must not share an entry.
    /// </summary>
    public string CacheKey => IsTrimmed ? $"{FilePath}|{StartMs}|{EndMs}" : FilePath;

    /// <summary>
    /// Wait this long before the sound starts, in milliseconds. -1 means use the board's
    /// default.
    ///
    /// This is a lead in, not the audio buffer. It exists for push to talk: the key has to
    /// be down and the channel open before the clip starts, or the first word is eaten.
    /// How long that takes depends on the app on the other end, so it is a setting rather
    /// than a constant, and it is per sound as well as global because a clip whose first
    /// moment is silence needs less of it than one that opens on a shout.
    /// </summary>
    public int DelayMs { get; set; } = UseDefaultDelay;

    /// <summary>Sentinel for "no override, follow the board's default".</summary>
    public const int UseDefaultDelay = -1;

    /// <summary>True when this button overrides the board's default delay.</summary>
    public bool HasOwnDelay => DelayMs >= 0;

    public Hotkey Hotkey { get; set; } = new();

    /// <summary>Position in the grid. Buttons are laid out in this order.</summary>
    public int Order { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? (string.IsNullOrWhiteSpace(FilePath) ? "Empty" : Path.GetFileNameWithoutExtension(FilePath))
            : Name;
}
