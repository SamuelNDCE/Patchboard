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

    public Hotkey Hotkey { get; set; } = new();

    /// <summary>Position in the grid. Buttons are laid out in this order.</summary>
    public int Order { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? (string.IsNullOrWhiteSpace(FilePath) ? "Empty" : Path.GetFileNameWithoutExtension(FilePath))
            : Name;
}
