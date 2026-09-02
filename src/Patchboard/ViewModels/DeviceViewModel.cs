using Patchboard.Models;
using Patchboard.Services;

namespace Patchboard.ViewModels;

/// <summary>
/// One row in the output or input panel: a tick box, a volume slider and a level meter.
///
/// Wraps both the live endpoint (<see cref="AudioDeviceInfo"/>, absent when the device is
/// unplugged) and the saved <see cref="AudioDeviceRef"/>, so a device the user had ticked
/// stays visible and greyed out instead of quietly vanishing from the list along with
/// their routing.
/// </summary>
public sealed class DeviceViewModel : ObservableObject
{
    private bool _isEnabled;
    private float _volume = 1f;
    private float _peak;
    private bool _receivesMic;
    private bool _isMonitor;

    public DeviceViewModel(AudioDeviceInfo? info, AudioDeviceRef reference)
    {
        Info = info;
        Reference = reference;
        _isEnabled = reference.Enabled;
        _volume = reference.Volume;
        _receivesMic = reference.ReceivesMic;
        _isMonitor = reference.IsMonitor;
    }

    /// <summary>True for a playback device. Only outputs can be sent the microphone.</summary>
    public bool IsOutput { get; init; }

    public AudioDeviceInfo? Info { get; }

    public AudioDeviceRef Reference { get; }

    public string Id => Reference.Id;

    public string FriendlyName => Info?.FriendlyName ?? Reference.FriendlyName;

    /// <summary>Device is in the saved config but not currently present on the machine.</summary>
    public bool IsMissing => Info is null;

    /// <summary>Device is physically there. Drives whether the row can be ticked.</summary>
    public bool IsPresent => Info is not null;

    public bool IsDefault => Info?.IsDefault ?? false;

    /// <summary>A virtual cable rather than real hardware. This is the route into Discord or a game.</summary>
    public bool IsVirtual => Info?.IsVirtual ?? false;

    /// <summary>Short tag shown next to the name, so the right entry is obvious at a glance.</summary>
    public string Tag =>
        IsMissing ? "not connected"
        : IsDefault ? "default"
        : IsVirtual ? "virtual" : "";

    public bool HasTag => Tag.Length > 0;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            Reference.Enabled = value;
            OnPropertyChanged(nameof(IsQuiet));
            EnabledChanged?.Invoke(this);
        }
    }

    /// <summary>0 to 1. Applied inside our own mix, never to the Windows device volume.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (!Set(ref _volume, clamped)) return;
            Reference.Volume = clamped;
            OnPropertyChanged(nameof(IsQuiet));
            VolumeChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Turned down far enough that the device looks broken rather than quiet.
    ///
    /// This is a real failure mode, not a hypothetical: a device left at 6% read as "the
    /// soundboard does not work", because a thin slider near its left end is hard to tell
    /// from one at any other low value.
    /// </summary>
    public bool IsQuiet => _isEnabled && _volume < 0.15f;

    /// <summary>Live level, 0 to 1, refreshed on a timer.</summary>
    public float Peak
    {
        get => _peak;
        set => Set(ref _peak, value);
    }

    /// <summary>
    /// Output devices only: send the live microphone here as well as the sounds.
    ///
    /// Off by default on every device. Turning it on for something audible in the room is
    /// how you get feedback, so the UI warns rather than assuming.
    /// </summary>
    public bool ReceivesMic
    {
        get => _receivesMic;
        set
        {
            if (!Set(ref _receivesMic, value)) return;
            Reference.ReceivesMic = value;
            OnPropertyChanged(nameof(FeedbackRisk));
            OnPropertyChanged(nameof(HasFeedbackRisk));
            MicRoutingChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Warning shown when sending the mic here is likely to howl.
    ///
    /// Real hardware is the obvious case: your voice comes out of the speakers next to
    /// the microphone. The subtler case is a virtual device that is itself routed back to
    /// speakers, which is what Samuel's "Voicemeeter Input" does through bus A1. We cannot
    /// read another app's routing, so the wording says what to check rather than claiming
    /// to know.
    /// </summary>
    public string? FeedbackRisk => !_receivesMic ? null : Info switch
    {
        { IsVirtual: false } => "You will hear yourself, and your mic will pick that up again",
        { IsVirtual: true } => "Safe only if this route does not come back out of your speakers",
        _ => null,
    };

    public bool HasFeedbackRisk => FeedbackRisk is not null;

    /// <summary>
    /// This device is the user's own headphones. Right clicking a sound and choosing
    /// "Play in my headphones" sends it here alone, so it can be checked without going
    /// out to Discord. Only one device holds this at a time.
    /// </summary>
    public bool IsMonitor
    {
        get => _isMonitor;
        set
        {
            if (!Set(ref _isMonitor, value)) return;
            Reference.IsMonitor = value;
            MonitorChanged?.Invoke(this);
        }
    }

    /// <summary>Set without raising the event, for clearing the flag on the other devices.</summary>
    public void ClearMonitorQuietly()
    {
        if (!_isMonitor) return;
        _isMonitor = false;
        Reference.IsMonitor = false;
        OnPropertyChanged(nameof(IsMonitor));
    }

    public event Action<DeviceViewModel>? MonitorChanged;

    public event Action<DeviceViewModel>? MicRoutingChanged;

    /// <summary>
    /// What Discord, OBS or a game must be set to listen on for this route to reach anyone.
    ///
    /// Picking the send side is only half a route, and getting it wrong is silent: the app
    /// happily plays into a cable whose other end nothing is reading. That is exactly what
    /// happened with CABLE Input on a machine where VoiceMeeter, not VB-Cable, carries the
    /// microphone. Null for real hardware, where the question does not arise.
    /// </summary>
    public string? ListenHint => FriendlyName switch
    {
        var n when n.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)
            => "Others hear this only if their mic is set to CABLE Output",
        var n when n.StartsWith("CABLE In 16ch", StringComparison.OrdinalIgnoreCase)
            => "Others hear this only if their mic is set to CABLE Output",
        var n when n.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
                   && n.Contains("Input", StringComparison.OrdinalIgnoreCase)
            => "Others hear this on Voicemeeter Out B1, B2 or B3, if that strip is routed to B",
        var n when n.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase)
            => "Only reaches Steam Remote Play, not Discord or a game",
        _ => null,
    };

    public bool HasListenHint => ListenHint is not null;

    public event Action<DeviceViewModel>? EnabledChanged;

    public event Action<DeviceViewModel>? VolumeChanged;
}
