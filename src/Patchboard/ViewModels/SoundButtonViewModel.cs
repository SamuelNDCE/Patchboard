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
    /// Paint this button. The parameter is "#RRGGBB", or empty to clear.
    ///
    /// It lives here rather than on MainViewModel because the context menu inherits its
    /// DataContext from the tile, so this view model is already in scope and the colour is
    /// the only other thing needed. The first attempt routed it through MainViewModel and
    /// had to carry the target and the colour together in an x:Array, which WPF rejects at
    /// layout time: a Binding cannot live inside an ArrayList, only on a DependencyProperty.
    /// </summary>
    public RelayCommand SetColorCommand => _setColor ??= new RelayCommand(
        parameter => Color = parameter as string is { Length: > 0 } hex ? hex : null);

    private RelayCommand? _setColor;

    // ---- Trim -------------------------------------------------------------------
    //
    // Sliders over the real clip length, not typed numbers. Typing a millisecond offset
    // means knowing where in the clip the moment is, which is the thing you were trying to
    // find in the first place. The length has to be read off the file before any of this
    // can be aimed, so the sliders stay disabled until it is known.

    private double _durationSeconds;
    private bool _durationRequested;

    /// <summary>Full length of the file in seconds, or 0 until it has been read.</summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (!Set(ref _durationSeconds, value)) return;
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(StartSeconds));
            OnPropertyChanged(nameof(EndSeconds));
            OnPropertyChanged(nameof(TrimText));
        }
    }

    public bool HasDuration => _durationSeconds > 0;

    public string DurationText => _durationSeconds > 0 ? $"{_durationSeconds:0.0}s long" : "reading length...";

    /// <summary>
    /// Read the clip's length so the sliders have a range.
    ///
    /// Off the UI thread and only once per button. It is a header read rather than a
    /// decode, so it is milliseconds, but it is still file IO reached from opening a menu
    /// and there is no reason to make the menu wait for a disk.
    /// </summary>
    public async Task EnsureDurationAsync()
    {
        if (_durationRequested || HasProblem) return;
        _durationRequested = true;

        var path = Model.FilePath;

        var seconds = await Task.Run(() =>
        {
            try
            {
                using var reader = new NAudio.Wave.AudioFileReader(path);
                return reader.TotalTime.TotalSeconds;
            }
            catch (Exception)
            {
                return 0d;
            }
        }).ConfigureAwait(true);

        if (seconds > 0) DurationSeconds = seconds;
    }

    /// <summary>Start of the kept window, in seconds.</summary>
    public double StartSeconds
    {
        get => Model.StartMs / 1000.0;
        set
        {
            var seconds = Clamp(value);

            // Never let the start cross the end. A zero length window would decode to
            // nothing and the button would go silent with no explanation.
            var end = Model.EndMs > 0 ? Model.EndMs / 1000.0 : _durationSeconds;
            if (end > 0 && seconds > end - MinWindowSeconds) seconds = Math.Max(0, end - MinWindowSeconds);

            var ms = (int)Math.Round(seconds * 1000);
            if (ms == Model.StartMs) return;

            Model.StartMs = ms;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrimText));
            OnPropertyChanged(nameof(IsTrimmed));
            TrimChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// End of the kept window, in seconds. Stored as 0 when it sits at the end of the
    /// file, so a clip that is later replaced by a longer one still plays in full.
    /// </summary>
    public double EndSeconds
    {
        get => Model.EndMs > 0 ? Model.EndMs / 1000.0 : _durationSeconds;
        set
        {
            var seconds = Clamp(value);

            var start = Model.StartMs / 1000.0;
            if (seconds < start + MinWindowSeconds) seconds = start + MinWindowSeconds;

            // Dragged to the far end means "play to the end", which is 0 rather than a
            // number that would go stale if the file changed.
            var atEnd = _durationSeconds > 0 && seconds >= _durationSeconds - 0.05;
            var ms = atEnd ? 0 : (int)Math.Round(seconds * 1000);
            if (ms == Model.EndMs) return;

            Model.EndMs = ms;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrimText));
            OnPropertyChanged(nameof(IsTrimmed));
            TrimChanged?.Invoke(this);
        }
    }

    /// <summary>Shortest window worth keeping. Below this a clip is a click.</summary>
    private const double MinWindowSeconds = 0.1;

    private double Clamp(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return 0;
        return _durationSeconds > 0 ? Math.Min(seconds, _durationSeconds) : seconds;
    }

    public bool IsTrimmed => Model.IsTrimmed;

    /// <summary>What the trim is doing, in words, under the sliders.</summary>
    public string TrimText
    {
        get
        {
            if (!HasDuration) return "";
            if (!Model.IsTrimmed) return $"Whole clip, {_durationSeconds:0.0}s";

            var end = Model.EndMs > 0 ? Model.EndMs / 1000.0 : _durationSeconds;
            var kept = end - Model.StartMs / 1000.0;
            return $"{Model.StartMs / 1000.0:0.0}s to {end:0.0}s  ({kept:0.0}s of {_durationSeconds:0.0}s)";
        }
    }

    /// <summary>Put the whole clip back.</summary>
    public RelayCommand ClearTrimCommand => _clearTrim ??= new RelayCommand(() =>
    {
        if (!Model.IsTrimmed) return;
        Model.StartMs = 0;
        Model.EndMs = 0;
        OnPropertyChanged(nameof(StartSeconds));
        OnPropertyChanged(nameof(EndSeconds));
        OnPropertyChanged(nameof(TrimText));
        OnPropertyChanged(nameof(IsTrimmed));
        TrimChanged?.Invoke(this);
    });

    private RelayCommand? _clearTrim;

    public event Action<SoundButtonViewModel>? TrimChanged;

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
