using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Patchboard.Models;
using Patchboard.Services;

namespace Patchboard.ViewModels;

/// <summary>
/// Application state and the wiring between the UI and the audio services.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Everything AudioFileReader can open. Used by the file picker and drag and drop.</summary>
    public static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif"];

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    private readonly ConfigService _configService = new();
    private readonly DeviceService _deviceService = new();
    private readonly AudioEngine _engine;
    private readonly MicCaptureService _mic;
    private readonly HotkeyService _hotkeys = new();
    private readonly DispatcherTimer _timer;

    /// <summary>Decoded audio, keyed by file path. A clip is decoded once per session.</summary>
    private readonly Dictionary<string, CachedSound> _cache = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config;
    private string _searchText = "";
    private string _status = "";
    private bool _settingsOpen;
    private bool _isBindingHotkey;
    private SoundButtonViewModel? _bindTarget;
    private bool _isRenaming;
    private string _renameText = "";
    private string _renameSubject = "";
    private SoundButtonViewModel? _renameTarget;

    public MainViewModel()
    {
        _config = _configService.Load();
        _engine = new AudioEngine(_deviceService);
        _mic = new MicCaptureService(_deviceService, _engine);

        Sounds = new ObservableCollection<SoundButtonViewModel>(
            _config.Sounds.OrderBy(s => s.Order).Select(s => new SoundButtonViewModel(s)));

        SoundsView = CollectionViewSource.GetDefaultView(Sounds);
        SoundsView.Filter = FilterSound;

        Outputs = [];
        Inputs = [];
        RefreshDevices();

        _engine.MasterVolume = _config.MasterVolume;
        ApplyOutputs();
        ApplyMic();

        PlayCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) Play(vm); });
        StopAllCommand = new RelayCommand(StopAll);
        AddSoundsCommand = new RelayCommand(PickSounds);
        RemoveSoundCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) Remove(vm); });
        RenameCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) BeginRename(vm); });
        ConfirmRenameCommand = new RelayCommand(ConfirmRename);
        CancelRenameCommand = new RelayCommand(CancelRename);
        SetImageCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) PickImage(vm); });
        ClearImageCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) ClearImage(vm); });
        BeginBindHotkeyCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) BeginBind(vm); });
        ClearHotkeyCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) ClearHotkey(vm); });
        RefreshDevicesCommand = new RelayCommand(() => { RefreshDevices(); ApplyOutputs(); ApplyMic(); });
        ImportResananceCommand = new RelayCommand(ImportResanance);
        ImportFolderCommand = new RelayCommand(ImportFolder);
        UseDefaultOutputCommand = new RelayCommand(() => EnableOutput(Outputs.FirstOrDefault(o => o.IsDefault)));
        UseCableOutputCommand = new RelayCommand(() => EnableOutput(CableOutput));
        ToggleSettingsCommand = new RelayCommand(() => SettingsOpen = !SettingsOpen);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ColumnsUpCommand = new RelayCommand(() => GridColumns++);
        ColumnsDownCommand = new RelayCommand(() => GridColumns--);
        RowsUpCommand = new RelayCommand(() => GridRows++);
        RowsDownCommand = new RelayCommand(() => GridRows--);

        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        // 60ms is fast enough that the progress line looks continuous and the meters feel
        // live, without the grid redrawing more often than a person can perceive.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _timer.Tick += OnTick;
        _timer.Start();

        Status = Sounds.Count == 0
            ? "Drop audio files anywhere to add them."
            : $"{Sounds.Count} sounds loaded.";
    }

    // ---- Collections ------------------------------------------------------------

    public ObservableCollection<SoundButtonViewModel> Sounds { get; }

    public ICollectionView SoundsView { get; }

    public ObservableCollection<DeviceViewModel> Outputs { get; }

    public ObservableCollection<DeviceViewModel> Inputs { get; }

    // ---- Commands ---------------------------------------------------------------

    public RelayCommand PlayCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand AddSoundsCommand { get; }
    public RelayCommand RemoveSoundCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand ConfirmRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand SetImageCommand { get; }
    public RelayCommand ClearImageCommand { get; }
    public RelayCommand BeginBindHotkeyCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ImportResananceCommand { get; }
    public RelayCommand ImportFolderCommand { get; }
    public RelayCommand UseDefaultOutputCommand { get; }
    public RelayCommand UseCableOutputCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ColumnsUpCommand { get; }
    public RelayCommand ColumnsDownCommand { get; }
    public RelayCommand RowsUpCommand { get; }
    public RelayCommand RowsDownCommand { get; }

    // ---- Simple state -----------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) SoundsView.Refresh(); }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => Set(ref _settingsOpen, value);
    }

    /// <summary>True while waiting for the next key press to bind it to a button.</summary>
    public bool IsBindingHotkey
    {
        get => _isBindingHotkey;
        private set => Set(ref _isBindingHotkey, value);
    }

    public int GridColumns
    {
        get => _config.GridColumns;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (_config.GridColumns == clamped) return;
            _config.GridColumns = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public int GridRows
    {
        get => _config.GridRows;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (_config.GridRows == clamped) return;
            _config.GridRows = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public float MasterVolume
    {
        get => _config.MasterVolume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_config.MasterVolume - clamped) < 0.0001f) return;
            _config.MasterVolume = clamped;
            _engine.MasterVolume = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public bool MicEnabled
    {
        get => _config.MicPassthroughEnabled;
        set
        {
            if (_config.MicPassthroughEnabled == value) return;
            _config.MicPassthroughEnabled = value;
            OnPropertyChanged();
            ApplyMic();
            NotifyOutputState();
            Save();
        }
    }

    public int LatencyMs
    {
        get => _config.LatencyMs;
        set
        {
            var clamped = Math.Clamp(value, 20, 500);
            if (_config.LatencyMs == clamped) return;
            _config.LatencyMs = clamped;
            OnPropertyChanged();
            ApplyOutputs();
            Save();
        }
    }

    /// <summary>Called from the window once it has a handle.</summary>
    public void AttachWindow(System.Windows.Window window)
    {
        _hotkeys.Attach(window);
        RegisterAllHotkeys();
    }

    // ---- Devices ----------------------------------------------------------------

    /// <summary>
    /// Rebuild both device lists from the live endpoints, merged with what the config
    /// remembers. A saved device that is no longer present stays in the list marked as
    /// not connected, so unplugging a headset does not silently erase the routing.
    /// </summary>
    public void RefreshDevices()
    {
        Merge(Outputs, _deviceService.ListOutputs(), _config.OutputDevices, OnOutputChanged);
        Merge(Inputs, _deviceService.ListInputs(), _config.InputDevices, OnInputChanged);
    }

    private static void Merge(
        ObservableCollection<DeviceViewModel> target,
        IReadOnlyList<AudioDeviceInfo> live,
        List<AudioDeviceRef> saved,
        Action<DeviceViewModel> onChanged)
    {
        target.Clear();

        foreach (var info in live)
        {
            var reference = saved.FirstOrDefault(s => s.Id == info.Id);
            if (reference is null)
            {
                reference = new AudioDeviceRef { Id = info.Id, FriendlyName = info.FriendlyName };
                saved.Add(reference);
            }

            // The name may have changed since it was saved. Keep the display current.
            reference.FriendlyName = info.FriendlyName;

            var vm = new DeviceViewModel(info, reference);
            vm.EnabledChanged += onChanged;
            vm.VolumeChanged += onChanged;
            target.Add(vm);
        }

        // Saved but absent, and only if the user had actually selected it. An unticked
        // device that is gone is just noise.
        foreach (var reference in saved.Where(s => s.Enabled && live.All(l => l.Id != s.Id)))
        {
            var vm = new DeviceViewModel(null, reference);
            vm.EnabledChanged += onChanged;
            vm.VolumeChanged += onChanged;
            target.Add(vm);
        }
    }

    private void OnOutputChanged(DeviceViewModel vm)
    {
        // A volume change on an already open device is applied live. Ticking a device on
        // or off has to reopen the streams, which is heavier, so the two are separated.
        if (Outputs.Any(o => o.IsEnabled) && vm.IsEnabled && _engine.FailedOutputs.Count == 0)
            _engine.SetDeviceVolume(vm.Id, vm.Volume);

        ApplyOutputs();
        Save();
    }

    private void OnInputChanged(DeviceViewModel vm)
    {
        ApplyMic();
        NotifyOutputState();
        Save();
    }

    /// <summary>Tick one output on the user's behalf, from the banner's shortcut buttons.</summary>
    private void EnableOutput(DeviceViewModel? device)
    {
        if (device is null) return;

        // Setting IsEnabled runs the normal path: it updates the saved reference, reopens
        // the streams and persists, exactly as if the checkbox had been clicked.
        device.IsEnabled = true;
    }

    /// <summary>
    /// Nothing is ticked in the output panel, so pressing a button does nothing at all.
    ///
    /// This is the single most likely way for the app to look broken, and it happened on
    /// first use: the input section was filled in and the output section left empty, so
    /// every press was silent. A line in the status bar was not enough, hence the banner.
    /// </summary>
    public bool HasNoOutput => Outputs.All(o => !o.IsEnabled);

    public bool HasDefaultOutput => Outputs.Any(o => o.IsDefault);

    public string DefaultOutputName =>
        Outputs.FirstOrDefault(o => o.IsDefault)?.FriendlyName ?? "default device";

    /// <summary>The endpoint that carries sound into Discord or a game as a microphone.</summary>
    private DeviceViewModel? CableOutput =>
        Outputs.FirstOrDefault(o =>
            o.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

    public bool HasCableOutput => CableOutput is not null;

    /// <summary>
    /// Mic passthrough is on and an output is selected, so his voice is now going wherever
    /// the sounds go. Worth saying out loud, because if that output is his headphones he
    /// will hear himself, and if VoiceMeeter is already routing the mic he goes out twice.
    /// </summary>
    public bool MicIsLive => _config.MicPassthroughEnabled && !HasNoOutput
                             && _config.InputDevices.Any(i => i.Enabled);

    private void NotifyOutputState()
    {
        OnPropertyChanged(nameof(HasNoOutput));
        OnPropertyChanged(nameof(HasDefaultOutput));
        OnPropertyChanged(nameof(DefaultOutputName));
        OnPropertyChanged(nameof(HasCableOutput));
        OnPropertyChanged(nameof(MicIsLive));
    }

    private void ApplyOutputs()
    {
        _engine.SetOutputs(_config.OutputDevices, _config.LatencyMs);
        NotifyOutputState();

        var enabled = _config.OutputDevices.Count(o => o.Enabled);
        if (enabled == 0)
        {
            Status = "No output device selected. Tick one on the left or nothing will play.";
            return;
        }

        if (_engine.FailedOutputs.Count > 0)
        {
            var first = _engine.FailedOutputs[0];
            Status = $"{first.FriendlyName}: {first.Error}";
            return;
        }

        Status = enabled == 1 ? "Playing to 1 device." : $"Playing to {enabled} devices at once.";
    }

    private void ApplyMic()
    {
        _engine.SetMicEnabled(_config.MicPassthroughEnabled);

        if (!_config.MicPassthroughEnabled)
        {
            _mic.Stop();
            return;
        }

        _mic.Start(_config.InputDevices);

        if (_mic.Failures.Count > 0)
        {
            var first = _mic.Failures[0];
            Status = $"{first.FriendlyName}: {first.Error}";
        }
    }

    // ---- Playback ---------------------------------------------------------------

    public void Play(SoundButtonViewModel vm)
    {
        if (_config.OutputDevices.All(o => !o.Enabled))
        {
            Status = "No output device selected. Tick one on the left.";
            return;
        }

        CachedSound sound;
        try
        {
            sound = GetOrLoad(vm.Model.FilePath);
        }
        catch (Exception ex)
        {
            vm.FileMissing = true;
            Status = ex.Message;
            return;
        }

        vm.FileMissing = false;
        var handle = _engine.Play(vm.Id, sound, vm.Model.Volume, vm.Model.Retrigger);

        // A null handle from a Toggle press means it stopped rather than started.
        if (handle is null && vm.Model.Retrigger == RetriggerMode.Toggle) vm.IsPlaying = false;
    }

    private CachedSound GetOrLoad(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var loaded = CachedSound.Load(path);
        _cache[path] = loaded;
        return loaded;
    }

    public void StopAll()
    {
        _engine.StopAll();
        foreach (var sound in Sounds) { sound.IsPlaying = false; sound.Progress = 0; }
        Status = "Stopped.";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var active = _engine.ActiveSounds;

        foreach (var vm in Sounds)
        {
            var playing = active.FirstOrDefault(a => a.ButtonId == vm.Id);
            var isPlaying = playing is not null;
            if (vm.IsPlaying != isPlaying) vm.IsPlaying = isPlaying;
            if (isPlaying) vm.Progress = playing!.Progress;
            else if (vm.Progress != 0) vm.Progress = 0;
        }

        foreach (var (deviceId, peak) in _engine.ReadOutputPeaks())
        {
            var device = Outputs.FirstOrDefault(o => o.Id == deviceId);
            if (device is not null) device.Peak = peak;
        }

        if (!_config.MicPassthroughEnabled) return;

        foreach (var (deviceId, peak) in _mic.ReadInputPeaks())
        {
            var device = Inputs.FirstOrDefault(i => i.Id == deviceId);
            if (device is not null) device.Peak = peak;
        }
    }

    // ---- Library ----------------------------------------------------------------

    private bool FilterSound(object item)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        return item is SoundButtonViewModel vm
               && vm.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void PickSounds()
    {
        var filter = "Audio files|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|All files|*.*";
        var dialog = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "Add sounds" };
        if (dialog.ShowDialog() != true) return;
        AddFiles(dialog.FileNames);
    }

    /// <summary>Add audio files, from the picker or from a drop. Silently skips duplicates.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;

        foreach (var path in Expand(paths))
        {
            if (Sounds.Any(s => string.Equals(s.Model.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var model = new SoundButton
            {
                FilePath = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Order = Sounds.Count,
            };

            _config.Sounds.Add(model);
            Sounds.Add(new SoundButtonViewModel(model));
            added++;
        }

        if (added > 0) Save();

        Status = added == 0
            ? (skipped > 0 ? "Already added." : "No supported audio files in that drop.")
            : $"Added {added} sound{(added == 1 ? "" : "s")}." + (skipped > 0 ? $" {skipped} already there." : "");
    }

    /// <summary>Turn a mixed drop of files and folders into a flat list of audio file paths.</summary>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                IEnumerable<string> found;
                try
                {
                    found = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    // An unreadable folder should not abort the rest of the drop.
                    continue;
                }

                foreach (var file in found.Where(IsAudio)) yield return file;
            }
            else if (File.Exists(path) && IsAudio(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsAudio(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void Remove(SoundButtonViewModel vm)
    {
        _hotkeys.Unregister(vm.Id);
        _config.Sounds.Remove(vm.Model);
        Sounds.Remove(vm);
        Reorder();
        Save();
        Status = $"Removed {vm.DisplayName}.";
    }

    private void Reorder()
    {
        for (var i = 0; i < Sounds.Count; i++) Sounds[i].Model.Order = i;
    }

    /// <summary>
    /// Drop <paramref name="dragged"/> onto <paramref name="target"/>'s position.
    ///
    /// Works on the underlying collection rather than on view indices, so reordering
    /// still lands correctly while a search filter is hiding some of the buttons.
    /// </summary>
    public void MoveSound(SoundButtonViewModel dragged, SoundButtonViewModel target)
    {
        if (ReferenceEquals(dragged, target)) return;

        var from = Sounds.IndexOf(dragged);
        var to = Sounds.IndexOf(target);
        if (from < 0 || to < 0) return;

        Sounds.Move(from, to);
        Reorder();
        Save();
        Status = $"Moved {dragged.DisplayName}.";
    }

    // ---- Rename -----------------------------------------------------------------

    /// <summary>True while the rename box is open.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set => Set(ref _isRenaming, value);
    }

    /// <summary>Bound to the rename text box.</summary>
    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value);
    }

    /// <summary>Shown above the rename box so it is obvious which button is being renamed.</summary>
    public string RenameSubject
    {
        get => _renameSubject;
        private set => Set(ref _renameSubject, value);
    }

    private void BeginRename(SoundButtonViewModel vm)
    {
        _renameTarget = vm;
        RenameSubject = Path.GetFileName(vm.Model.FilePath);
        RenameText = vm.DisplayName;
        IsRenaming = true;
    }

    private void ConfirmRename()
    {
        if (_renameTarget is null) { IsRenaming = false; return; }

        var name = RenameText.Trim();
        var target = _renameTarget;
        _renameTarget = null;
        IsRenaming = false;

        // An empty name falls back to the file name rather than leaving a blank button.
        target.Model.Name = name;
        target.Refresh();
        Save();
        SoundsView.Refresh();

        Status = $"Renamed to {target.DisplayName}.";
    }

    private void CancelRename()
    {
        _renameTarget = null;
        IsRenaming = false;
    }

    private void PickImage(SoundButtonViewModel vm)
    {
        var filter = "Images|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|All files|*.*";
        var dialog = new OpenFileDialog { Filter = filter, Title = $"Image for {vm.DisplayName}" };
        if (dialog.ShowDialog() != true) return;

        vm.Model.ImagePath = dialog.FileName;
        vm.Refresh();
        Save();
    }

    private void ClearImage(SoundButtonViewModel vm)
    {
        vm.Model.ImagePath = null;
        vm.Refresh();
        Save();
    }

    // ---- Hotkeys ----------------------------------------------------------------

    private void RegisterAllHotkeys()
    {
        _hotkeys.UnregisterAll();

        var conflicts = new List<string>();

        foreach (var vm in Sounds.Where(s => s.Model.Hotkey.IsSet))
        {
            if (!_hotkeys.Register(vm.Id, vm.Model.Hotkey, out var error))
                conflicts.Add($"{vm.DisplayName} ({vm.HotkeyText}): {error}");
        }

        if (_config.StopAllHotkey.IsSet
            && !_hotkeys.Register("stop-all", _config.StopAllHotkey, out var stopError))
        {
            conflicts.Add($"Stop all ({_config.StopAllHotkey}): {stopError}");
        }

        if (conflicts.Count > 0)
            Status = $"{conflicts.Count} hotkey{(conflicts.Count == 1 ? "" : "s")} rejected by Windows. {conflicts[0]}";
    }

    private void OnHotkeyPressed(string id)
    {
        if (id == "stop-all") { StopAll(); return; }
        var vm = Sounds.FirstOrDefault(s => s.Id == id);
        if (vm is not null) Play(vm);
    }

    private void BeginBind(SoundButtonViewModel vm)
    {
        _bindTarget = vm;
        IsBindingHotkey = true;
        Status = $"Press a key combination for {vm.DisplayName}. Escape to cancel.";
    }

    /// <summary>
    /// Called by the window while <see cref="IsBindingHotkey"/> is true. Returns true when
    /// the press was consumed, so the window can mark the event handled.
    /// </summary>
    public bool TryCompleteBind(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!IsBindingHotkey || _bindTarget is null) return false;

        if (key == System.Windows.Input.Key.Escape)
        {
            IsBindingHotkey = false;
            _bindTarget = null;
            Status = "Binding cancelled.";
            return true;
        }

        var hotkey = KeyNames.FromWpfKey(key, modifiers);
        if (hotkey is null) return true; // a modifier on its own, keep waiting

        var target = _bindTarget;
        _bindTarget = null;
        IsBindingHotkey = false;

        // Whoever held this combo before loses it, otherwise two buttons fire as one.
        foreach (var other in Sounds.Where(s => s != target && s.Model.Hotkey.ToString() == hotkey.ToString()))
        {
            other.Model.Hotkey = new Hotkey();
            other.Refresh();
        }

        target.Model.Hotkey = hotkey;
        target.Refresh();
        Save();
        RegisterAllHotkeys();

        if (Status.StartsWith("Press a key", StringComparison.Ordinal))
            Status = $"{target.DisplayName} bound to {hotkey}.";

        return true;
    }

    private void ClearHotkey(SoundButtonViewModel vm)
    {
        vm.Model.Hotkey = new Hotkey();
        vm.Refresh();
        _hotkeys.Unregister(vm.Id);
        Save();
        Status = $"Cleared the binding on {vm.DisplayName}.";
    }

    // ---- Import -----------------------------------------------------------------

    private void ImportResanance()
    {
        if (!ResananceImporter.IsAvailable)
        {
            Status = "No Resanance library found on this machine.";
            return;
        }

        var result = new ResananceImporter().ImportFromDatabase();
        Absorb(result, "Resanance");
    }

    private void ImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Import a folder of sounds" };
        if (dialog.ShowDialog() != true) return;

        var result = new ResananceImporter().ImportFromFolder(dialog.FolderName);
        Absorb(result, Path.GetFileName(dialog.FolderName));
    }

    private void Absorb(ImportResult result, string source)
    {
        var added = 0;

        foreach (var model in result.Sounds)
        {
            if (Sounds.Any(s => string.Equals(s.Model.FilePath, model.FilePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            model.Order = Sounds.Count;
            _config.Sounds.Add(model);
            Sounds.Add(new SoundButtonViewModel(model));
            added++;
        }

        if (added > 0) Save();

        Status = added == 0
            ? $"Nothing new from {source}." + (result.Warnings.Count > 0 ? $" {result.Warnings[0]}" : "")
            : $"Imported {added} sound{(added == 1 ? "" : "s")} from {source}."
              + (result.Skipped > 0 ? $" {result.Skipped} skipped, the files are gone." : "");
    }

    // ---- Persistence ------------------------------------------------------------

    private void Save()
    {
        Reorder();
        try
        {
            _configService.Save(_config);
        }
        catch (Exception ex)
        {
            Status = $"Could not save settings: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Save();
        _hotkeys.Dispose();
        _mic.Dispose();
        _engine.Dispose();
        _deviceService.Dispose();
    }
}
