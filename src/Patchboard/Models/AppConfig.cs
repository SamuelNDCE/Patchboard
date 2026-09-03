namespace Patchboard.Models;

/// <summary>
/// The whole persisted state of the app. Written to
/// <c>%APPDATA%\Patchboard\config.json</c> atomically on every change.
///
/// Deliberately a plain JSON file rather than an embedded database. Resanance uses
/// LiteDB and its own log on this machine is full of "the process cannot access the
/// file because it is being used by another process", which stops the app starting.
/// A single JSON document with an atomic replace cannot deadlock itself that way.
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<SoundButton> Sounds { get; set; } = new();

    /// <summary>Playback targets. Sound is written to every enabled entry simultaneously.</summary>
    public List<AudioDeviceRef> OutputDevices { get; set; } = new();

    /// <summary>Microphones mixed into the outputs when <see cref="MicPassthroughEnabled"/> is on.</summary>
    public List<AudioDeviceRef> InputDevices { get; set; } = new();

    /// <summary>
    /// Off by default. Samuel runs VoiceMeeter, which may already be routing his mic;
    /// enabling this without checking would send his voice twice.
    /// </summary>
    public bool MicPassthroughEnabled { get; set; }

    public int GridColumns { get; set; } = 8;

    public int GridRows { get; set; } = 7;

    /// <summary>Master gain applied after per-sound and per-device volume.</summary>
    public float MasterVolume { get; set; } = 1.0f;

    /// <summary>Global "shut everything up" binding.</summary>
    public Hotkey StopAllHotkey { get; set; } = new();

    /// <summary>WASAPI shared-mode buffer in milliseconds. Lower is snappier, riskier.</summary>
    public int LatencyMs { get; set; } = 60;

    /// <summary>
    /// Lead in before a sound starts, in milliseconds, for buttons that do not set their
    /// own. Zero by default, because a soundboard should be instant unless told otherwise.
    /// </summary>
    public int DefaultDelayMs { get; set; }

    // Window placement. Restored on launch so the app comes back where it was left,
    // which matters here because it lives on a second monitor.
    public double WindowWidth { get; set; } = 1240;

    public double WindowHeight { get; set; } = 780;

    /// <summary>NaN means "never positioned", so let Windows choose.</summary>
    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public bool WindowMaximized { get; set; }
}
