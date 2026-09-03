using Patchboard.Models;

namespace Patchboard.ViewModels;

/// <summary>One cell in the grid.</summary>
public sealed class SoundButtonViewModel : ObservableObject
{
    private bool _isPlaying;
    private double _progress;
    private string? _problem;
    private string? _problemDetail;
    private bool _isLoading;

    public SoundButtonViewModel(SoundButton model)
    {
        Model = model;
        if (IsFileGone(model)) _problem = "file missing";
    }

    private static bool IsFileGone(SoundButton model) =>
        !string.IsNullOrWhiteSpace(model.FilePath) && !File.Exists(model.FilePath);

    public SoundButton Model { get; }

    public string Id => Model.Id;

    public string DisplayName => Model.DisplayName;

    public string HotkeyText => Model.Hotkey.ToString();

    public bool HasHotkey => Model.Hotkey.IsSet;

    public string? ImagePath =>
        !string.IsNullOrWhiteSpace(Model.ImagePath) && File.Exists(Model.ImagePath)
            ? Model.ImagePath
            : null;

    public bool HasImage => ImagePath is not null;

    /// <summary>
    /// Tile background as "#RRGGBB", or null for the default surface.
    ///
    /// This was saved, exposed and bound to the tile from the start, and nothing in the
    /// app could set it, so 206 imported buttons were all the same grey. Colour is the
    /// only way to tell tiles apart at a glance when the labels are long enough to clip.
    /// </summary>
    public string? Color
    {
        get => Model.Color;
        set
        {
            if (Model.Color == value) return;
            Model.Color = value;
            OnPropertyChanged();
            ColorChanged?.Invoke(this);
        }
    }

    /// <summary>Raised so the view model that owns persistence can save.</summary>
    public event Action<SoundButtonViewModel>? ColorChanged;

    /// <summary>
    /// Start of the kept window, in seconds, as text so it can be typed.
    ///
    /// Seconds rather than milliseconds because nobody types 8300, and text rather than a
    /// slider because the clip length is not known until the file has been opened and a
    /// slider over an unknown range is not something you can aim.
    /// </summary>
    public string StartText
    {
        get => Model.StartMs == 0 ? "" : (Model.StartMs / 1000.0).ToString("0.##");
        set
        {
            var ms = ParseSeconds(value);
            if (ms == Model.StartMs) return;
            Model.StartMs = ms;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrimText));
            TrimChanged?.Invoke(this);
        }
    }

    /// <summary>End of the kept window, in seconds. Empty means play to the end.</summary>
    public string EndText
    {
        get => Model.EndMs == 0 ? "" : (Model.EndMs / 1000.0).ToString("0.##");
        set
        {
            var ms = ParseSeconds(value);
            if (ms == Model.EndMs) return;
            Model.EndMs = ms;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrimText));
            TrimChanged?.Invoke(this);
        }
    }

    /// <summary>A one line summary of the trim for the menu, or empty when there is none.</summary>
    public string TrimText => Model.IsTrimmed
        ? $"Playing {(Model.StartMs / 1000.0):0.##}s to " +
          (Model.EndMs > 0 ? $"{(Model.EndMs / 1000.0):0.##}s" : "the end")
        : "";

    public event Action<SoundButtonViewModel>? TrimChanged;

    /// <summary>
    /// Seconds as typed to whole milliseconds. Anything unparseable, negative or absurd
    /// becomes 0, which means "no trim on this end" rather than an error dialog: this runs
    /// on every keystroke in the box.
    /// </summary>
    private static int ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out var seconds)
            && !double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            return 0;
        }

        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        return (int)Math.Min(seconds * 1000, int.MaxValue);
    }

    /// <summary>
    /// Per-sound gain, 0 to 2. Above 1 is a real boost for a quiet recording.
    /// </summary>
    public float Volume
    {
        get => Model.Volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, SoundButton.MaxVolume);
            if (Math.Abs(Model.Volume - clamped) < 0.0001f) return;
            Model.Volume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(IsBoosted));
            VolumeChanged?.Invoke(this);
        }
    }

    public string VolumeText => $"{Model.Volume * 100:0}%";

    /// <summary>Above unity, where clipping becomes possible. The UI colours this.</summary>
    public bool IsBoosted => Model.Volume > 1.001f;

    /// <summary>
    /// Set by MainViewModel so the volume submenu can preview without walking up to the
    /// window. A submenu lives in its own popup, where an AncestorType lookup for the
    /// ContextMenu does not reliably resolve.
    /// </summary>
    public RelayCommand? PreviewCommand { get; set; }

    public event Action<SoundButtonViewModel>? VolumeChanged;

    /// <summary>Drives the accent edge and the progress line.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    /// <summary>
    /// Why this button cannot play, in two or three words, or null when it is fine.
    ///
    /// Shown as an overlay rather than removing the button, so the hotkey and the label
    /// survive and the sound can be repointed. It is a short label and not the full
    /// message because it has to fit inside a grid tile; <see cref="ProblemDetail"/>
    /// carries the sentence, and the status bar carries it too.
    ///
    /// A missing file is not the only way a button can be dead, which is what this
    /// replaced: a clip too long to decode was also reported as "file missing", sending
    /// the user to look for a file that was sitting exactly where they left it.
    /// </summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (!Set(ref _problem, value)) return;
            OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>
    /// The clip is being decoded on a background thread after a press.
    ///
    /// Only ever true for a clip that is not already in memory, so in practice it shows on
    /// the first press of a long one. It exists because the alternative to saying "opening"
    /// is a button that looks like it ignored the click.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    /// <summary>The full sentence behind <see cref="Problem"/>, for the tooltip.</summary>
    public string? ProblemDetail
    {
        get => _problemDetail;
        private set => Set(ref _problemDetail, value);
    }

    /// <summary>
    /// A two word label for the overlay on a button that will not play.
    ///
    /// Kept separate from the exception message because the tile is about 90 pixels wide.
    /// The distinction that matters is "go and find the file" versus "the file is exactly
    /// where you left it and the clip is unusable", because those send someone to
    /// completely different places. Three of the imported buttons are hour long music
    /// rips, and every one of them used to say "file missing".
    /// </summary>
    public static string DescribeProblem(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "file missing",
        // Matched on type, not on message text. These used to be one exception type told
        // apart by searching the message for "longer than", which is the kind of check
        // that breaks silently the first time someone rewords a sentence.
        Services.ClipTooLongException => "too long",
        Services.ClipEmptyException => "no audio",
        UnauthorizedAccessException => "no access",
        _ => "won't play",
    };

    /// <summary>Record why a press did nothing. <paramref name="detail"/> is shown on hover.</summary>
    public void SetProblem(string label, string detail)
    {
        Problem = label;
        ProblemDetail = detail;
    }

    /// <summary>The button played, so whatever was wrong with it no longer is.</summary>
    public void ClearProblem()
    {
        Problem = null;
        ProblemDetail = null;
    }

    /// <summary>Re-read everything the model owns after an edit.</summary>
    public void Refresh()
    {
        if (IsFileGone(Model)) SetProblem("file missing", $"{Model.FilePath} is not there any more.");
        else if (Problem == "file missing") ClearProblem();

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Color));
    }
}
