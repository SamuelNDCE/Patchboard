using Patchboard.Models;

namespace Patchboard.ViewModels;

/// <summary>One cell in the grid.</summary>
public sealed class SoundButtonViewModel : ObservableObject
{
    private bool _isPlaying;
    private double _progress;
    private string? _problem;
    private string? _problemDetail;

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

    public string? Color => Model.Color;

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
        // CachedSound throws this both for a clip past the decode ceiling and for one
        // that decodes to no audio at all.
        InvalidOperationException when ex.Message.Contains("longer than") => "too long",
        InvalidOperationException => "no audio",
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
