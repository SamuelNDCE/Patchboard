using Patchboard.Models;

namespace Patchboard.ViewModels;

/// <summary>One cell in the grid.</summary>
public sealed class SoundButtonViewModel : ObservableObject
{
    private bool _isPlaying;
    private double _progress;
    private bool _fileMissing;

    public SoundButtonViewModel(SoundButton model)
    {
        Model = model;
        _fileMissing = !string.IsNullOrWhiteSpace(model.FilePath) && !File.Exists(model.FilePath);
    }

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
    /// The file was deleted or moved since it was bound. Shown as a struck through button
    /// rather than removed, so the user can repoint it instead of losing the hotkey.
    /// </summary>
    public bool FileMissing
    {
        get => _fileMissing;
        set => Set(ref _fileMissing, value);
    }

    /// <summary>Re-read everything the model owns after an edit.</summary>
    public void Refresh()
    {
        FileMissing = !string.IsNullOrWhiteSpace(Model.FilePath) && !File.Exists(Model.FilePath);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Color));
    }
}
