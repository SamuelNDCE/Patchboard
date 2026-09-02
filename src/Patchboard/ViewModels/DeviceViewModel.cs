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

    public DeviceViewModel(AudioDeviceInfo? info, AudioDeviceRef reference)
    {
        Info = info;
        Reference = reference;
        _isEnabled = reference.Enabled;
        _volume = reference.Volume;
    }

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
            VolumeChanged?.Invoke(this);
        }
    }

    /// <summary>Live level, 0 to 1, refreshed on a timer.</summary>
    public float Peak
    {
        get => _peak;
        set => Set(ref _peak, value);
    }

    public event Action<DeviceViewModel>? EnabledChanged;

    public event Action<DeviceViewModel>? VolumeChanged;
}
