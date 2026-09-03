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

    /// <summary>
    /// Whether the live microphone is mixed into this device. Off unless the user asked
    /// for it, because sending the mic somewhere audible feeds back.
    /// </summary>
    public bool ReceivesMic { get; set; }

    /// <summary>
    /// Live microphone fan-out sink for this device.
    ///
    /// Created on the first opt-in and then kept for the life of the channel, including
    /// while the mic is off. It has to be kept: NAudio's mixer has no way to take an
    /// input back out without the exact provider instance that was added, so a sink that
    /// was dropped and recreated on every toggle left the old one wired to the mixer
    /// forever. Twenty flicks of the checkbox measured twenty dead inputs, each one still
    /// read by the WASAPI render thread on every single buffer. One permanent sink per
    /// device is both correct and cheaper.
    /// </summary>
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
    internal readonly List<(OutputChannel Channel, IPlaybackInstance Provider)> Instances = [];

    internal PlayingSound(string buttonId, float soundVolume)
    {
        ButtonId = buttonId;
        SoundVolume = soundVolume;
    }

    public string ButtonId { get; }

    public float SoundVolume { get; }

    /// <summary>
    /// How many output devices this sound is playing on. Public so a caller can tell a
    /// normal press, which reaches every device, from a preview, which reaches exactly one.
    /// </summary>
    public int DeviceCount => Instances.Count;

    /// <summary>True once every device's copy has finished or faded out.</summary>
    public bool IsFinished => Instances.All(i => i.Provider.IsFinished);

    /// <summary>0 to 1 through the clip, for the progress line under the button.</summary>
    public double Progress => Instances.Count == 0 ? 0 : Instances.Max(i => i.Provider.Progress);

    /// <summary>How far through the clip this sound is, as a time.</summary>
    public TimeSpan Position => Duration * Progress;

    /// <summary>Total length of the sound being played, after any trim.</summary>
    public TimeSpan Duration { get; internal init; }

    /// <summary>What is playing, for the transport bar.</summary>
    public string DisplayName { get; internal init; } = "";

    /// <summary>Fade out and end on every device.</summary>
    public void Stop()
    {
        foreach (var (_, provider) in Instances) provider.RequestStop();
    }

    /// <summary>
    /// Jump every device's copy to the same fraction through the clip.
    ///
    /// They move together rather than independently, because they are one sound as far as
    /// the listener is concerned and letting them drift would put an echo on it.
    /// </summary>
    public void Seek(double fraction)
    {
        foreach (var (_, provider) in Instances) provider.Seek(fraction);
    }

    /// <summary>Release anything the copies hold, such as a streamed file's handle.</summary>
    internal void DisposeInstances()
    {
        foreach (var (_, provider) in Instances) provider.Dispose();
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

            foreach (var sound in _playing) { sound.Stop(); sound.DisposeInstances(); }
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
            ReceivesMic = reference.ReceivesMic,
        };
    }

    /// <summary>Turn the microphone on or off for a single output device.</summary>
    public void SetMicRouting(string deviceId, bool receivesMic)
    {
        lock (Gate)
        {
            var channel = _channels.FirstOrDefault(c => c.DeviceId == deviceId);
            if (channel is null) return;

            channel.ReceivesMic = receivesMic;

            if (!receivesMic)
            {
                // Clearing the buffer stops the tail of already captured audio from
                // trickling out after the user has asked for it to stop. The sink itself
                // stays wired to the mixer and simply goes quiet, because PushMicSamples
                // now gates on ReceivesMic rather than on this being null.
                channel.MicSink?.ClearBuffer();
                return;
            }

            if (_micEnabled) AttachMicSinks();
        }
    }

    /// <summary>True when at least one output is carrying the microphone right now.</summary>
    public bool AnyChannelReceivesMic
    {
        get { lock (Gate) { return _micEnabled && _channels.Any(c => c.ReceivesMic); } }
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
    /// <param name="onlyDeviceId">
    /// When given, the sound plays to that one device and no other. This is how previewing
    /// to your own headphones works without the sound also going out to Discord.
    /// </param>
    public PlayingSound? Play(
        string buttonId,
        ISoundSource source,
        float soundVolume,
        RetriggerMode retrigger,
        string? onlyDeviceId = null,
        string displayName = "")
    {
        lock (Gate)
        {
            Reap();

            var existing = _playing.Where(p => p.ButtonId == buttonId).ToList();
            switch (retrigger)
            {
                case RetriggerMode.Toggle when existing.Count > 0:
                    foreach (var sounding in existing) { sounding.Stop(); sounding.DisposeInstances(); }
                    _playing.RemoveAll(p => p.ButtonId == buttonId);
                    return null;

                case RetriggerMode.Restart:
                    foreach (var sounding in existing) { sounding.Stop(); sounding.DisposeInstances(); }
                    _playing.RemoveAll(p => p.ButtonId == buttonId);
                    break;
            }

            var targets = onlyDeviceId is null
                ? _channels
                : _channels.Where(c => c.DeviceId == onlyDeviceId).ToList();

            if (targets.Count == 0) return null;

            var handle = new PlayingSound(buttonId, Math.Clamp(soundVolume, 0f, SoundButton.MaxVolume))
            {
                Duration = source.Duration,
                DisplayName = displayName,
            };

            foreach (var channel in targets)
            {
                // A streamed source opens a file here, which can fail on a drive that has
                // just gone away. One dead device must not stop the others sounding.
                IPlaybackInstance provider;
                try
                {
                    provider = source.CreateInstance(handle.SoundVolume * channel.Volume * _masterVolume);
                }
                catch (Exception)
                {
                    continue;
                }

                channel.Mixer.AddMixerInput(provider);
                handle.Instances.Add((channel, provider));
            }

            if (handle.Instances.Count == 0) return null;

            _playing.Add(handle);
            return handle;
        }
    }

    /// <summary>Fade out everything currently sounding.</summary>
    public void StopAll()
    {
        lock (Gate)
        {
            foreach (var sound in _playing) { sound.Stop(); sound.DisposeInstances(); }
            _playing.Clear();
        }
    }

    /// <summary>
    /// The sound to show in the transport bar: the most recently started one still going.
    /// Null when nothing is playing.
    /// </summary>
    public PlayingSound? Current
    {
        get
        {
            lock (Gate)
            {
                for (var i = _playing.Count - 1; i >= 0; i--)
                    if (!_playing[i].IsFinished) return _playing[i];
                return null;
            }
        }
    }

    public IReadOnlyList<PlayingSound> ActiveSounds
    {
        get
        {
            lock (Gate)
            {
                Reap();
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
            // The safety check. A channel the user did not opt in stays mic free, so a
            // device they can hear cannot start a feedback loop on its own.
            if (!channel.ReceivesMic) continue;
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
        // Drop whatever is already buffered so the last half second of voice cannot
        // trickle out after the switch is off. The sinks stay wired to their mixers and
        // feed silence; PushMicSamples stops filling them while _micEnabled is false.
        foreach (var channel in _channels) channel.MicSink?.ClearBuffer();
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
            {
                // The sink outlives an opt-out, so this flag, not the sink's existence,
                // is what decides whether a device currently carries the voice.
                if (!channel.ReceivesMic) continue;
                channel.MicSink?.AddSamples(pcmFloat32Stereo48k);
            }
        }
    }

    /// <summary>
    /// How many providers each device's mixer reads on every buffer.
    ///
    /// This is the render thread's per buffer workload, and it is the number that catches
    /// a leak: it should be the count of sounds currently playing, plus one if that device
    /// has ever carried the mic, and it must come back down when they finish. A version of
    /// this engine grew one permanent input per flick of a mic checkbox, which nothing
    /// else observed.
    /// </summary>
    public IReadOnlyList<(string DeviceId, int MixerInputs)> ReadMixerLoad()
    {
        lock (Gate)
        {
            return _channels.Select(c => (c.DeviceId, c.Mixer.MixerInputs.Count())).ToList();
        }
    }

    /// <summary>Live output levels per device, for the meters in the device panel.</summary>
    public IReadOnlyList<(string DeviceId, float Peak)> ReadOutputPeaks()
    {
        // Snapshot under the lock, then read the meters outside it.
        //
        // MasterPeakValue is a cross-apartment COM call and this runs on a 60ms UI timer,
        // so holding Gate across it would park the microphone capture thread, which needs
        // the same lock in PushMicSamples, several times a second. The capture thread must
        // never wait on the UI for audio it has already recorded.
        OutputChannel[] channels;
        lock (Gate)
        {
            channels = [.. _channels];
        }

        var peaks = new List<(string, float)>(channels.Length);
        foreach (var channel in channels)
        {
            try
            {
                peaks.Add((channel.DeviceId, channel.Device.AudioMeterInformation.MasterPeakValue));
            }
            catch (Exception)
            {
                // The channel can be disposed out from under us between the snapshot and
                // the read, which surfaces as a COM failure on a dead object. Report no
                // level rather than tearing down the meter for every other device.
                peaks.Add((channel.DeviceId, 0f));
            }
        }

        return peaks;
    }

    /// <summary>
    /// Drop finished sounds, releasing whatever their copies hold. A streamed instance
    /// keeps a file handle and a decoder open, so leaving those to the garbage collector
    /// would hold files open for as long as it felt like.
    /// </summary>
    private void Reap()
    {
        for (var i = _playing.Count - 1; i >= 0; i--)
        {
            if (!_playing[i].IsFinished) continue;
            _playing[i].DisposeInstances();
            _playing.RemoveAt(i);
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
            foreach (var sound in _playing) { sound.Stop(); sound.DisposeInstances(); }
            _playing.Clear();
            foreach (var channel in _channels) channel.Dispose();
            _channels.Clear();
        }
    }
}
