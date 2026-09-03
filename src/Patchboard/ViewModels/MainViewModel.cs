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
    private readonly DispatcherTimer _volumeSaveTimer;
    private bool _pendingVolumeSave;

    /// <summary>
    /// Buttons currently drawn as playing. Lets an idle tick do nothing at all instead of
    /// proving, once per button, that nothing changed.
    /// </summary>
    private int _buttonsShowingPlaying;

    /// <summary>Files currently being decoded, so one button cannot start two decodes.</summary>
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the window is on screen. False while minimised, which is most of the time
    /// for a soundboard, and the level meters are not worth reading when nobody can see
    /// them. Playback and hotkeys are unaffected; only the drawing is.
    /// </summary>
    public bool UiVisible { get; set; } = true;

    /// <summary>
    /// Decoded audio, under a memory budget. A clip stays decoded while it is being used
    /// and is dropped once the board has moved on to others.
    /// </summary>
    private readonly SoundCache _cache = new();

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

        foreach (var sound in Sounds) Attach(sound);

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
        SetColorCommand = new RelayCommand(SetColor);
        BeginBindHotkeyCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) BeginBind(vm); });
        ClearHotkeyCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) ClearHotkey(vm); });
        RefreshDevicesCommand = new RelayCommand(() => { RefreshDevices(); ApplyOutputs(); ApplyMic(); });
        ImportResananceCommand = new RelayCommand(ImportResanance);
        ImportFolderCommand = new RelayCommand(ImportFolder);
        RemoveDeadCommand = new RelayCommand(RemoveDead);
        RemoveDuplicatesCommand = new RelayCommand(RemoveDuplicates);
        TidyNamesCommand = new RelayCommand(TidyNames);
        UseDefaultOutputCommand = new RelayCommand(() => EnableOutput(Outputs.FirstOrDefault(o => o.IsDefault)));
        UseCableOutputCommand = new RelayCommand(() => EnableOutput(VoiceRoute));
        MuteMicCommand = new RelayCommand(MuteMic);
        PreviewCommand = new RelayCommand(p => { if (p is SoundButtonViewModel vm) PreviewInHeadphones(vm); });
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

        _volumeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            if (!_pendingVolumeSave) return;
            _pendingVolumeSave = false;
            Save();
        };

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
    public RelayCommand SetColorCommand { get; }
    public RelayCommand BeginBindHotkeyCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ImportResananceCommand { get; }
    public RelayCommand ImportFolderCommand { get; }
    public RelayCommand RemoveDeadCommand { get; }
    public RelayCommand RemoveDuplicatesCommand { get; }
    public RelayCommand TidyNamesCommand { get; }
    public RelayCommand UseDefaultOutputCommand { get; }
    public RelayCommand UseCableOutputCommand { get; }
    public RelayCommand MuteMicCommand { get; }
    public RelayCommand PreviewCommand { get; }
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

    // ---- Window placement ---------------------------------------------------------

    /// <summary>Size and position to restore on launch. NaN position means never placed.</summary>
    public (double Width, double Height, double Left, double Top, bool Maximized) WindowPlacement =>
        (_config.WindowWidth, _config.WindowHeight, _config.WindowLeft, _config.WindowTop, _config.WindowMaximized);

    /// <summary>
    /// Remember where the window was. Saved on close rather than on every move, because a
    /// drag would otherwise rewrite the whole config file on every mouse tick.
    /// </summary>
    public void SaveWindowPlacement(double width, double height, double left, double top, bool maximized)
    {
        // Ignore a collapsed or off-screen result, which is what you get if the window was
        // minimised at the moment of closing. Restoring that would open it invisible.
        if (!maximized && (width < 200 || height < 200)) return;

        _config.WindowWidth = width;
        _config.WindowHeight = height;
        _config.WindowLeft = left;
        _config.WindowTop = top;
        _config.WindowMaximized = maximized;
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
        Merge(Outputs, _deviceService.ListOutputs(), _config.OutputDevices, OnOutputChanged, isOutput: true);
        foreach (var output in Outputs)
        {
            output.MicRoutingChanged += OnMicRoutingChanged;
            output.MonitorChanged += OnMonitorChanged;
        }
        Merge(Inputs, _deviceService.ListInputs(), _config.InputDevices, OnInputChanged, isOutput: false);
    }

    private static void Merge(
        ObservableCollection<DeviceViewModel> target,
        IReadOnlyList<AudioDeviceInfo> live,
        List<AudioDeviceRef> saved,
        Action<DeviceViewModel> onChanged,
        bool isOutput)
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

            var vm = new DeviceViewModel(info, reference) { IsOutput = isOutput };
            vm.EnabledChanged += onChanged;
            vm.VolumeChanged += onChanged;
            target.Add(vm);
        }

        // Saved but absent, and only if the user had actually selected it. An unticked
        // device that is gone is just noise.
        foreach (var reference in saved.Where(s => s.Enabled && live.All(l => l.Id != s.Id)))
        {
            var vm = new DeviceViewModel(null, reference) { IsOutput = isOutput };
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

    /// <summary>
    /// One device at a time is "my headphones". Marking a new one clears the old, and the
    /// device is switched on if it was not already: a preview plays through an existing
    /// stream, so an unticked monitor would silently do nothing.
    /// </summary>
    /// <summary>Give a tile the hooks it needs: preview, and a save when its volume moves.</summary>
    private void Attach(SoundButtonViewModel vm)
    {
        vm.PreviewCommand = new RelayCommand(() => PreviewInHeadphones(vm));
        vm.VolumeChanged += OnSoundVolumeChanged;
        vm.ColorChanged += OnSoundColorChanged;
        vm.TrimChanged += OnSoundTrimChanged;
    }

    /// <summary>
    /// Dragging a slider raises this on every tick. Writing the whole config each time
    /// would rewrite a file with 206 sounds in it dozens of times a second, so the save is
    /// deferred until the drag settles.
    /// </summary>
    private void OnSoundVolumeChanged(SoundButtonViewModel vm)
    {
        _pendingVolumeSave = true;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
        Status = $"{vm.DisplayName} at {vm.VolumeText}.";
    }

    /// <summary>
    /// Paint one button, or clear it back to the default surface.
    ///
    /// The parameter arrives from the swatch as "vm|#RRGGBB", or "vm|" to clear, because a
    /// WPF command carries a single parameter and the alternative was a command per colour.
    /// </summary>
    private void SetColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } pair) return;
        if (pair[0] is not SoundButtonViewModel vm) return;

        var colour = pair[1] as string;
        vm.Color = string.IsNullOrWhiteSpace(colour) ? null : colour;
    }

    private void OnSoundColorChanged(SoundButtonViewModel vm)
    {
        Save();
        Status = vm.Color is null ? $"{vm.DisplayName} back to the default colour." : $"{vm.DisplayName} recoloured.";
    }

    /// <summary>
    /// The trim moved, so whatever is cached under the old window is now the wrong audio.
    ///
    /// Both keys are dropped: the one it had before this edit is unknown here, so the
    /// untrimmed key is cleared too. Re-decoding one clip costs a few hundred milliseconds
    /// on a background thread; playing the wrong few seconds costs a take.
    /// </summary>
    private void OnSoundTrimChanged(SoundButtonViewModel vm)
    {
        _cache.Remove(vm.Model.CacheKey);
        _cache.Remove(vm.Model.FilePath);

        // Same debounce as the volume slider: this fires on every keystroke in the box.
        _pendingVolumeSave = true;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();

        Status = vm.Model.IsTrimmed
            ? $"{vm.DisplayName}: {vm.TrimText}."
            : $"{vm.DisplayName} plays in full again.";
    }

    private void OnMonitorChanged(DeviceViewModel vm)
    {
        if (vm.IsMonitor)
        {
            foreach (var other in Outputs.Where(o => !ReferenceEquals(o, vm))) other.ClearMonitorQuietly();
            if (!vm.IsEnabled) vm.IsEnabled = true;
        }

        OnPropertyChanged(nameof(HasMonitor));
        OnPropertyChanged(nameof(MonitorName));
        Save();
    }

    /// <summary>The device previews play to, when one has been marked.</summary>
    public DeviceViewModel? Monitor => Outputs.FirstOrDefault(o => o.IsMonitor && o.IsEnabled);

    public bool HasMonitor => Monitor is not null;

    public string MonitorName => Monitor?.FriendlyName ?? "";

    /// <summary>
    /// Play a sound to the monitor device only, so it can be checked without everyone in
    /// Discord hearing it.
    /// </summary>
    public async void PreviewInHeadphones(SoundButtonViewModel vm)
    {
        var monitor = Monitor;
        if (monitor is null)
        {
            // Distinguish "never chose one" from "chose one and then switched it off",
            // because the fix is different and the second reads as a bug otherwise.
            var markedButOff = Outputs.FirstOrDefault(o => o.IsMonitor && !o.IsEnabled);

            Status = markedButOff is not null
                ? $"{markedButOff.FriendlyName} is set as your headphones but is switched off. Tick it under OUTPUT."
                : "No headphones set. Tick a device under OUTPUT, then tick 'these are my headphones' under it.";
            return;
        }

        var sound = await Decode(vm);
        if (sound is null) return;

        // Always Restart for a preview. Stacking copies of the same clip while auditioning
        // it is never what someone wants.
        _engine.Play(vm.Id, sound, vm.Model.Volume, RetriggerMode.Restart, monitor.Id);
        Status = $"Previewing {vm.DisplayName} in {monitor.FriendlyName} only.";
    }

    private void OnMicRoutingChanged(DeviceViewModel vm)
    {
        _engine.SetMicRouting(vm.Id, vm.ReceivesMic);
        NotifyOutputState();
        Save();
    }

    /// <summary>
    /// Kill the microphone everywhere, immediately.
    ///
    /// This is the panic button. Feedback builds fast and gets loud, and hunting for the
    /// right checkbox while it howls is not a reasonable thing to ask of anyone.
    /// </summary>
    private void MuteMic()
    {
        MicEnabled = false;
        Status = "Microphone off. Your voice is no longer going anywhere.";
    }

    /// <summary>
    /// A capture device and an output device that are two ends of the same virtual cable.
    ///
    /// Recording the far end of a cable you are playing into feeds the signal round and
    /// round with no acoustic gap to damp it, which is louder and faster than ordinary
    /// speaker feedback. Named pairs only: guessing at another app's internal routing
    /// would produce false alarms, so this reports what it can actually prove.
    /// </summary>
    public string? LoopWarning
    {
        get
        {
            if (!_config.MicPassthroughEnabled) return null;

            var sending = Outputs.Where(o => o.IsEnabled && o.ReceivesMic).Select(o => o.FriendlyName).ToList();
            if (sending.Count == 0) return null;

            var capturing = Inputs.Where(i => i.IsEnabled).Select(i => i.FriendlyName).ToList();

            foreach (var output in sending)
            {
                var pair = output switch
                {
                    var n when n.StartsWith("CABLE In", StringComparison.OrdinalIgnoreCase) => "CABLE Output",
                    var n when n.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
                               && n.Contains("Input", StringComparison.OrdinalIgnoreCase) => "Voicemeeter Out",
                    _ => null,
                };

                if (pair is null) continue;

                var offender = capturing.FirstOrDefault(c => c.StartsWith(pair, StringComparison.OrdinalIgnoreCase));
                if (offender is not null)
                    return $"Feedback loop: you are sending the mic to {output} while also recording {offender}. Untick one.";
            }

            return null;
        }
    }

    public bool HasLoopWarning => LoopWarning is not null;

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

    /// <summary>
    /// Something is ticked, but everything ticked is real hardware, so the sound comes out
    /// of speakers or headphones and reaches nobody else.
    ///
    /// This is a completely different failure from having nothing ticked, and it is the
    /// one that actually happened twice. Resanance had exactly this state, which is why it
    /// "did not work" and why Patchboard exists; then Patchboard ended up in it too, with
    /// only Realtek speakers enabled. In both cases the app looked correctly configured,
    /// every press made a noise, and not one person on the other end heard anything.
    ///
    /// Deliberately not raised while nothing is ticked at all, because HasNoOutput already
    /// covers that and two banners saying different things about the same panel is worse
    /// than one saying the right thing.
    /// </summary>
    public bool OnlyLocalOutput =>
        !HasNoOutput && Outputs.Where(o => o.IsEnabled).All(o => !o.IsVirtual);

    /// <summary>
    /// Names what is currently ticked, so the warning can never be vague about which
    /// device it means.
    /// </summary>
    public string LocalOutputNames =>
        string.Join(", ", Outputs.Where(o => o.IsEnabled).Select(o => o.FriendlyName));

    public bool HasDefaultOutput => Outputs.Any(o => o.IsDefault);

    public string DefaultOutputName =>
        Outputs.FirstOrDefault(o => o.IsDefault)?.FriendlyName ?? "default device";

    /// <summary>
    /// The endpoint most likely to actually reach Discord or a game as a microphone.
    ///
    /// VoiceMeeter comes first, and that order is the whole point. Both VB-Cable and
    /// VoiceMeeter are usually installed together, but only one of them is carrying the
    /// user's microphone, and the other is a cable with nothing attached to its far end.
    /// This shortcut originally offered CABLE Input unconditionally and sent Samuel to a
    /// dead end: his VoiceMeeter reads his USB mic and routes it to B1, while nothing on
    /// the machine reads CABLE Output at all. Where VoiceMeeter is present it is the safer
    /// guess, because its virtual input reaches the same bus his voice already uses.
    /// </summary>
    private DeviceViewModel? VoiceRoute =>
        Outputs.FirstOrDefault(o =>
            o.FriendlyName.StartsWith("Voicemeeter Input", StringComparison.OrdinalIgnoreCase))
        ?? Outputs.FirstOrDefault(o =>
            o.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));

    public bool HasCableOutput => VoiceRoute is not null;

    /// <summary>Names the device on the button, so the shortcut cannot misrepresent itself.</summary>
    public string VoiceRouteName => VoiceRoute?.FriendlyName ?? "";

    /// <summary>
    /// Mic passthrough is on and an output is selected, so his voice is now going wherever
    /// the sounds go. Worth saying out loud, because if that output is his headphones he
    /// will hear himself, and if VoiceMeeter is already routing the mic he goes out twice.
    /// </summary>
    public bool MicIsLive => _config.MicPassthroughEnabled
                             && _config.InputDevices.Any(i => i.Enabled)
                             && Outputs.Any(o => o.IsEnabled && o.ReceivesMic);

    private void NotifyOutputState()
    {
        OnPropertyChanged(nameof(HasNoOutput));
        OnPropertyChanged(nameof(OnlyLocalOutput));
        OnPropertyChanged(nameof(LocalOutputNames));
        OnPropertyChanged(nameof(HasDefaultOutput));
        OnPropertyChanged(nameof(DefaultOutputName));
        OnPropertyChanged(nameof(HasCableOutput));
        OnPropertyChanged(nameof(VoiceRouteName));
        OnPropertyChanged(nameof(LoopWarning));
        OnPropertyChanged(nameof(HasLoopWarning));
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

    public async void Play(SoundButtonViewModel vm)
    {
        if (_config.OutputDevices.All(o => !o.Enabled))
        {
            Status = "No output device selected. Tick one on the left.";
            return;
        }

        var sound = await Decode(vm);
        if (sound is null) return;

        var handle = _engine.Play(vm.Id, sound, vm.Model.Volume, vm.Model.Retrigger);

        // A null handle from a Toggle press means it stopped rather than started.
        if (handle is null && vm.Model.Retrigger == RetriggerMode.Toggle) vm.IsPlaying = false;
    }

    /// <summary>
    /// Get the decoded clip for a button, decoding it off the UI thread if it is not
    /// already in memory. Returns null when it cannot be played, having already put the
    /// reason on the button and in the status bar.
    ///
    /// Decoding used to run inline in the click handler, which froze the whole window for
    /// as long as it took. Measured against the real library: 1.8 seconds for a ten minute
    /// clip, and 4.4 seconds for an hour long one that was then rejected anyway. The
    /// window did not redraw, the grid did not scroll, and nothing said why.
    ///
    /// A clip already in the cache returns without ever awaiting, so the common press
    /// still reaches the mixer in the same message pump turn and stays instant.
    /// </summary>
    private async Task<CachedSound?> Decode(SoundButtonViewModel vm)
    {
        var path = vm.Model.FilePath;

        // Keyed by the trim as well as the path: the same file trimmed two ways is two
        // different pieces of audio and must not share one cache entry.
        var key = vm.Model.CacheKey;

        if (_cache.TryGet(key, out var cached))
        {
            vm.ClearProblem();
            return cached;
        }

        // A second press while the first is still decoding must not start a second decode
        // of the same file, which on a big clip would double the work and the memory.
        if (!_loading.Add(key)) return null;

        var startMs = vm.Model.StartMs;
        var endMs = vm.Model.EndMs;

        vm.IsLoading = true;
        try
        {
            var sound = await Task.Run(() => CachedSound.Load(path, startMs, endMs)).ConfigureAwait(true);

            // Back on the UI thread, which is the only thread SoundCache is safe on.
            _cache.Add(key, sound);
            vm.ClearProblem();
            return sound;
        }
        catch (Exception ex)
        {
            vm.SetProblem(SoundButtonViewModel.DescribeProblem(ex), ex.Message);
            Status = ex.Message;
            return null;
        }
        finally
        {
            vm.IsLoading = false;
            _loading.Remove(key);
        }
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

        // An idle soundboard is the normal case: it sits behind a game for hours with
        // nothing playing. Walking all 206 buttons and running a LINQ scan for each of
        // them, seventeen times a second, to conclude that nothing changed is the kind of
        // background cost that has no symptom and no excuse. Skip it outright unless
        // something is playing now or was on the previous tick.
        if (active.Count > 0 || _buttonsShowingPlaying > 0)
        {
            var showing = 0;
            foreach (var vm in Sounds)
            {
                PlayingSound? playing = null;
                foreach (var candidate in active)
                {
                    if (candidate.ButtonId != vm.Id) continue;
                    playing = candidate;
                    break;
                }

                var isPlaying = playing is not null;
                if (isPlaying) showing++;
                if (vm.IsPlaying != isPlaying) vm.IsPlaying = isPlaying;
                if (isPlaying) vm.Progress = playing!.Progress;
                else if (vm.Progress != 0) vm.Progress = 0;
            }

            _buttonsShowingPlaying = showing;
        }

        // Meters are only worth reading when someone can see them. While the window is
        // minimised, which is where a soundboard spends most of its life, this skips a
        // COM call per device seventeen times a second.
        if (!UiVisible) return;

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
            var vm = new SoundButtonViewModel(model);
            Attach(vm);
            Sounds.Add(vm);
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
                // One level deep, matching ResananceImporter.ImportFromFolder rather than
                // recursing without limit. A sound library is a folder of clips with a few
                // themed subfolders; dropping Downloads on the window used to walk the
                // entire tree and put a music collection on the board.
                foreach (var file in AudioFilesIn(path)) yield return file;

                foreach (var child in SubfoldersOf(path))
                    foreach (var file in AudioFilesIn(child))
                        yield return file;
            }
            else if (File.Exists(path) && IsAudio(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>Audio files directly in one folder. An unreadable folder yields nothing.</summary>
    private static IEnumerable<string> AudioFilesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(IsAudio).ToList();
        }
        catch (Exception)
        {
            // A permission denied folder must not abort the rest of the drop. Materialised
            // with ToList inside the try because a lazy sequence would throw later, out
            // where nothing is catching it.
            return [];
        }
    }

    private static IEnumerable<string> SubfoldersOf(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception)
        {
            return [];
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

    // ---- Library tidying ---------------------------------------------------------
    //
    // All three of these change many buttons at once, so all three ask first and name the
    // exact number. None of them touches a file on disk.

    /// <summary>Drop every button whose audio file is gone.</summary>
    private void RemoveDead()
    {
        var dead = LibraryTidy.FindDead(Sounds.Select(v => v.Model));
        if (dead.Count == 0)
        {
            Status = "Every button still points at a file that exists.";
            return;
        }

        if (!Confirm($"Remove {Count(dead.Count, "button")} whose file is missing?\n\n" +
                     Sample(dead.Select(d => d.DisplayName)) +
                     "\n\nThe audio files are not touched. Only the buttons go."))
            return;

        RemoveModels(dead);
        Status = $"Removed {Count(dead.Count, "dead button")}.";
    }

    /// <summary>Keep one button per distinct clip and drop the rest.</summary>
    private void RemoveDuplicates()
    {
        Status = "Checking for duplicates...";

        var groups = LibraryTidy.FindDuplicates(Sounds.Select(v => v.Model));
        var extras = groups.SelectMany(g => g.Skip(1)).ToList();

        if (extras.Count == 0)
        {
            Status = "No duplicates. Every button is a different clip.";
            return;
        }

        if (!Confirm($"{Count(groups.Count, "clip")} appear more than once. " +
                     $"Remove the {Count(extras.Count, "extra button")}?\n\n" +
                     Sample(extras.Select(d => d.DisplayName)) +
                     "\n\nOne button is kept for each clip, and no audio file is deleted."))
            return;

        RemoveModels(extras);
        Status = $"Removed {Count(extras.Count, "duplicate")}, kept one of each.";
    }

    /// <summary>Clean download artefacts out of labels that were never renamed by hand.</summary>
    private void TidyNames()
    {
        var plan = LibraryTidy.PlanNameTidy(Sounds.Select(v => v.Model));
        if (plan.Count == 0)
        {
            Status = "Nothing to tidy. Every label is already readable or was set by hand.";
            return;
        }

        var preview = string.Join("\n", plan.Take(6).Select(x =>
            $"{Path.GetFileNameWithoutExtension(x.Sound.FilePath)}\n    becomes  {x.NewName}"));

        if (!Confirm($"Tidy {Count(plan.Count, "label")}?\n\n{preview}" +
                     (plan.Count > 6 ? $"\n\n...and {plan.Count - 6} more." : "") +
                     "\n\nLabels you renamed yourself are left alone."))
            return;

        foreach (var (sound, newName) in plan) sound.Name = newName;
        foreach (var vm in Sounds) vm.Refresh();

        Save();
        Status = $"Tidied {Count(plan.Count, "label")}.";
    }

    private void RemoveModels(IReadOnlyCollection<SoundButton> models)
    {
        var doomed = new HashSet<string>(models.Select(m => m.Id), StringComparer.Ordinal);

        foreach (var vm in Sounds.Where(v => doomed.Contains(v.Id)).ToList())
        {
            _hotkeys.Unregister(vm.Id);
            Sounds.Remove(vm);
        }

        _config.Sounds.RemoveAll(m => doomed.Contains(m.Id));
        Reorder();
        Save();
    }

    /// <summary>
    /// A yes/no before anything that changes many buttons at once.
    ///
    /// Deliberately a real modal. These actions are not undoable from inside the app, and
    /// a status bar line after the fact is not consent.
    /// </summary>
    private static bool Confirm(string message) =>
        System.Windows.MessageBox.Show(
            message, "Patchboard",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Sample(IEnumerable<string> names)
    {
        var list = names.Take(6).ToList();
        return string.Join("\n", list.Select(n => "    " + n));
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
            var vm = new SoundButtonViewModel(model);
            Attach(vm);
            Sounds.Add(vm);
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
        _volumeSaveTimer.Stop();
        Save();
        _hotkeys.Dispose();
        _mic.Dispose();
        _engine.Dispose();
        _deviceService.Dispose();
    }
}
