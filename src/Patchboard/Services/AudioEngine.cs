using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>One live output device with its own mixer, volume and WASAPI stream.</summary>
internal sealed class OutputChannel : IDisposable
{
    public required string DeviceId { get; init; }
    public required string FriendlyName { get; init; }
    public required MMDevice Device { get; init; }
    public required WasapiPlayer Output { get; init; }
    public required MixingSampleProvider Mixer { get; init; }

    /// <summary>Per-device gain from the UI, 0.0 to 1.0.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Live microphone fan-out sink for this device, when passthrough is on.</summary>
    public BufferedWaveProvider? MicSink { get; set; }

    public void Dispose()
    {
        try { Output.Stop(); } catch (Exception) { /* device may already be gone */ }
        try { Output.Dispose(); } catch (Exception) { }
        try { Device.Dispose(); } catch (Exception) { }
    }
}

/// <summary>A sound currently playing, possibly on several devices at once.</summary>
public sealed class PlayingSound
{
    internal readonly List<(OutputChannel Channel, CachedSoundSampleProvider Provider)> Instances = [];

    internal PlayingSound(string buttonId, float soundVolume)
    {
        ButtonId = buttonId;
        SoundVolume = soundVolume;
    }

    public string ButtonId { get; }

    public float SoundVolume { get; }

    /// <summary>True once every device's copy has finished or faded out.</summary>
    public bool IsFinished => Instances.All(i => i.Provider.IsFinished);

    /// <summary>0 to 1 through the clip, for the progress line under the button.</summary>
    public double Progress => Instances.Count == 0 ? 0 : Instances.Max(i => i.Provider.Progress);

    /// <summary>Fade out and end on every device.</summary>
    public void Stop()
    {
        foreach (var (_, provider) in Instances) provider.RequestStop();
    }
}

/// <summary>
/// Plays sounds to any number of output devices simultaneously.
///
/// One <see cref="OutputChannel"/> is held open per selected device for the lifetime of
/// the selection, each with a mixer that runs permanently and feeds silence when nothing
/// is playing. Keeping the streams open is what makes a button press instant: opening a
/// WASAPI stream costs tens of milliseconds, which on a soundboard is the difference
/// between landing a joke and missing it.
///
/// Thread safety: NAudio's MixingSampleProvider locks internally on add, remove and read,
/// so mixer inputs may be added from the UI thread while the WASAPI thread is reading.
/// The channel list itself is guarded by <see cref="Gate"/>.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Lock Gate = new();
    private readonly List<OutputChannel> _channels = [];
    private readonly List<PlayingSound> _playing = [];
    private readonly DeviceService _devices;

    private float _masterVolume = 1f;
    private bool _micEnabled;
    private int _latencyMs = 60;

    public AudioEngine(DeviceService devices) => _devices = devices;

    /// <summary>
    /// Devices that were selected but could not be opened, with the reason.
    /// Surfaced in the UI rather than swallowed, because a silently dead output is the
    /// single most confusing failure a soundboard can have.
    /// </summary>
    public IReadOnlyList<(string FriendlyName, string Error)> FailedOutputs { get; private set; } = [];

    public float MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = Math.Clamp(value, 0f, 1f); RefreshVolumes(); }
    }

    /// <summary>Rebuild the set of live output devices. Stops anything currently playing.</summary>
    public void SetOutputs(IEnumerable<AudioDeviceRef> outputs, int latencyMs)
    {
        lock (Gate)
        {
            _latencyMs = Math.Clamp(latencyMs, 20, 500);

            foreach (var sound in _playing) sound.Stop();
            _playing.Clear();

            foreach (var channel in _channels) channel.Dispose();
            _channels.Clear();

            var failures = new List<(string, string)>();

            foreach (var reference in outputs.Where(o => o.Enabled))
            {
                MMDevice? device = null;
                try
                {
                    device = _devices.Resolve(reference, DataFlow.Render);
                    if (device is null)
                    {
                        failures.Add((reference.FriendlyName, "Device not found. It may be unplugged or disabled."));
                        continue;
                    }

                    _channels.Add(OpenChannel(device, reference));
                    device = null; // ownership passed to the channel
                }
                catch (Exception ex)
                {
                    device?.Dispose();
                    failures.Add((reference.FriendlyName, Describe(ex)));
                }
            }

            FailedOutputs = failures;
            if (_micEnabled) AttachMicSinks();
        }
    }

    private OutputChannel OpenChannel(MMDevice device, AudioDeviceRef reference)
    {
        var mixer = new MixingSampleProvider(AudioFormat.Mix)
        {
            // Without this the mixer returns 0 when idle, WASAPI treats that as
            // end-of-stream and the device stops. It would play the first sound and
            // then go permanently silent.
            ReadFully = true,
        };

        // Shared mode is mandatory, not a preference. Exclusive mode seizes the endpoint
        // and would cut off Discord, the game, and everything else using it.
        //
        // MMCSS raises the render thread's priority, which matters here because several
        // devices are being fed at once and a late buffer on any one of them is an audible
        // dropout. No stream category is set deliberately: the Communications and
        // SoundEffects categories change how Windows ducks audio during calls, and picking
        // one wrongly would make the soundboard go quiet mid-call.
        var output = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(_latencyMs)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();

        // Ask the endpoint what it accepts rather than inferring it. A format the device
        // rejects is what makes an output initialise without error and then stay silent.
        ISampleProvider chain = mixer;
        if (!output.IsFormatSupported(AudioFormat.Mix))
        {
            var deviceFormat = output.DeviceMixFormat;

            if (deviceFormat.SampleRate != AudioFormat.SampleRate)
                chain = new WdlResamplingSampleProvider(chain, deviceFormat.SampleRate);

            if (deviceFormat.Channels > 2)
                chain = new StereoToMultiChannelSampleProvider(chain, deviceFormat.Channels);
            else if (deviceFormat.Channels == 1)
                chain = new StereoToMonoSampleProvider(chain);
        }

        output.Init(chain.ToWaveProvider());
        output.Play();

        return new OutputChannel
        {
            DeviceId = device.ID,
            FriendlyName = reference.FriendlyName,
            Device = device,
            Output = output,
            Mixer = mixer,
            Volume = Math.Clamp(reference.Volume, 0f, 1f),
        };
    }

    /// <summary>Update one device's gain, including on sounds already playing.</summary>
    public void SetDeviceVolume(string deviceId, float volume)
    {
        lock (Gate)
        {
            var channel = _channels.FirstOrDefault(c => c.DeviceId == deviceId);
            if (channel is null) return;
            channel.Volume = Math.Clamp(volume, 0f, 1f);
        }

        RefreshVolumes();
    }

    private void RefreshVolumes()
    {
        lock (Gate)
        {
            foreach (var sound in _playing)
            {
                foreach (var (channel, provider) in sound.Instances)
                    provider.Volume = sound.SoundVolume * channel.Volume * _masterVolume;
            }
        }
    }

    /// <summary>
    /// Start a sound on every live output device.
    /// Returns null when nothing started, either because no outputs are selected or
    /// because a toggle press stopped it. The caller reports that rather than letting a
    /// dead press look like it worked.
    /// </summary>
    public PlayingSound? Play(string buttonId, CachedSound sound, float soundVolume, RetriggerMode retrigger)
    {
        lock (Gate)
        {
            _playing.RemoveAll(p => p.IsFinished);

            var existing = _playing.Where(p => p.ButtonId == buttonId).ToList();
            switch (retrigger)
            {
                case RetriggerMode.Toggle when existing.Count > 0:
                    foreach (var sounding in existing) sounding.Stop();
                    _playing.RemoveAll(p => p.ButtonId == buttonId);
                    return null;

                case RetriggerMode.Restart:
                    foreach (var sounding in existing) sounding.Stop();
                    _playing.RemoveAll(p => p.ButtonId == buttonId);
                    break;
            }

            if (_channels.Count == 0) return null;

            var handle = new PlayingSound(buttonId, Math.Clamp(soundVolume, 0f, 1f));
            foreach (var channel in _channels)
            {
                var provider = new CachedSoundSampleProvider(
                    sound, handle.SoundVolume * channel.Volume * _masterVolume);
                channel.Mixer.AddMixerInput(provider);
                handle.Instances.Add((channel, provider));
            }

            _playing.Add(handle);
            return handle;
        }
    }

    /// <summary>Fade out everything currently sounding.</summary>
    public void StopAll()
    {
        lock (Gate)
        {
            foreach (var sound in _playing) sound.Stop();
            _playing.Clear();
        }
    }

    public IReadOnlyList<PlayingSound> ActiveSounds
    {
        get
        {
            lock (Gate)
            {
                _playing.RemoveAll(p => p.IsFinished);
                return _playing.ToList();
            }
        }
    }

    // ---- Microphone passthrough -------------------------------------------------
    //
    // A live microphone cannot be fanned out the way a cached clip can, because a buffer
    // is consumed by whichever reader gets there first. Each output device therefore
    // gets its own BufferedWaveProvider and every captured block is copied into all of
    // them.

    public void SetMicEnabled(bool enabled)
    {
        lock (Gate)
        {
            if (_micEnabled == enabled) return;
            _micEnabled = enabled;
            if (enabled) AttachMicSinks(); else DetachMicSinks();
        }
    }

    private void AttachMicSinks()
    {
        foreach (var channel in _channels)
        {
            if (channel.MicSink is not null) continue;

            var sink = new BufferedWaveProvider(AudioFormat.Mix, TimeSpan.FromMilliseconds(500))
            {
                // Silence rather than end-of-stream when the mic has not delivered yet.
                ReadFully = true,
                // A stalled mic must not back up and eventually throw. Drop instead.
                DiscardOnBufferOverflow = true,
            };

            channel.MicSink = sink;
            channel.Mixer.AddMixerInput(sink.ToSampleProvider());
        }
    }

    private void DetachMicSinks()
    {
        foreach (var channel in _channels)
        {
            if (channel.MicSink is null) continue;
            channel.MicSink.ClearBuffer();
            channel.MicSink = null;
        }

        // The mixer input is left in place feeding silence. Removing it would need the
        // wrapped provider reference, and an idle buffered provider costs nothing.
    }

    /// <summary>
    /// Push a block of captured microphone audio, already in the mix format, to every
    /// output device. Called from the capture thread.
    /// </summary>
    public void PushMicSamples(ReadOnlySpan<byte> pcmFloat32Stereo48k)
    {
        lock (Gate)
        {
            if (!_micEnabled) return;
            foreach (var channel in _channels)
                channel.MicSink?.AddSamples(pcmFloat32Stereo48k);
        }
    }

    /// <summary>Live output levels per device, for the meters in the device panel.</summary>
    public IReadOnlyList<(string DeviceId, float Peak)> ReadOutputPeaks()
    {
        lock (Gate)
        {
            var peaks = new List<(string, float)>(_channels.Count);
            foreach (var channel in _channels)
            {
                try
                {
                    peaks.Add((channel.DeviceId, channel.Device.AudioMeterInformation.MasterPeakValue));
                }
                catch (Exception)
                {
                    peaks.Add((channel.DeviceId, 0f));
                }
            }

            return peaks;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        COMException com =>
            $"Windows refused the device (0x{com.HResult:X8}). Another app may hold it in exclusive mode.",
        _ => ex.Message,
    };

    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var sound in _playing) sound.Stop();
            _playing.Clear();
            foreach (var channel in _channels) channel.Dispose();
            _channels.Clear();
        }
    }
}
