using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patchboard.Models;

namespace Patchboard.Services;

/// <summary>One live microphone: its WASAPI capture, its conversion chain and its meter.</summary>
internal sealed class MicChannel : IDisposable
{
    /// <summary>
    /// Guards the meter fields, and nothing else.
    ///
    /// It exists as a separate lock because the capture thread must never wait on the
    /// lock that guards the channel list. <see cref="WasapiCapture.Dispose"/> joins the
    /// capture thread, so a capture thread blocked on a lock the disposing thread holds
    /// would never exit and the join would never return.
    /// </summary>
    public readonly Lock PeakGate = new();

    public required string DeviceId { get; init; }
    public required string FriendlyName { get; init; }
    public required MMDevice Device { get; init; }

    // NAudio 3.0.1 marks WasapiCapture obsolete in favour of WasapiRecorderBuilder. Kept
    // deliberately: the replacement is an IAsyncEnumerable pull API, and this class is built
    // around the push callback. Suppressed narrowly so a different obsolete call still warns.
#pragma warning disable CS0618
    public required WasapiCapture Capture { get; init; }
#pragma warning restore CS0618

    /// <summary>Push side of the adapter, in the microphone's own capture format.</summary>
    public required BufferedWaveProvider Source { get; init; }

    /// <summary>Pull side of the adapter, always 48kHz stereo float by the time it ends.</summary>
    public required ISampleProvider Chain { get; init; }

    /// <summary>Bytes per frame of the capture format, for turning buffered bytes into frames.</summary>
    public required int SourceBlockAlign { get; init; }

    /// <summary>48000 divided by the capture rate, so input frames map to output frames.</summary>
    public required double RateRatio { get; init; }

    public required float[] Scratch { get; init; }

    /// <summary>Per-device gain from the UI, 0.0 to 1.0. Fixed for the life of the channel.</summary>
    public float Volume { get; init; } = 1f;

    /// <summary>Set before a deliberate teardown so the stop is not reported as a failure.</summary>
    public volatile bool Stopping;

    public float Peak;
    public long PeakStamp;

    public void Dispose()
    {
        Stopping = true;
        try { Capture.StopRecording(); } catch (Exception) { /* device may already be gone */ }
        try { Capture.Dispose(); } catch (Exception) { }
        try { Device.Dispose(); } catch (Exception) { }
        try { Source.ClearBuffer(); } catch (Exception) { }
    }
}

/// <summary>
/// Captures one or more real microphones and feeds them into <see cref="AudioEngine"/>, so
/// the user's voice and the soundboard arrive at the virtual cable together.
///
/// The awkward part is the impedance mismatch in the middle. WASAPI capture is push based
/// and hands us the device's own shared-mode format, which is very often mono and often
/// 44100 or 16000 Hz. NAudio's resampler is pull based and the engine only accepts 48kHz
/// stereo float. Each device therefore owns a small adapter: a BufferedWaveProvider that
/// the capture callback writes into, and a sample provider chain that the same callback
/// immediately pulls back out of and pushes to the engine.
///
/// Thread safety: the capture callbacks run on one WASAPI thread per device and touch only
/// that device's own buffers, so they need no lock for the audio path. They must never take
/// <see cref="Gate"/>. See <see cref="MicChannel.PeakGate"/> for why.
/// </summary>
public sealed class MicCaptureService : IDisposable
{
    /// <summary>WASAPI capture buffer. Lower is snappier voice, riskier under load.</summary>
    private const int CaptureBufferMs = 50;

    /// <summary>Headroom in the adapter. It is drained on every callback, so it stays near empty.</summary>
    private const int SourceBufferMs = 500;

    /// <summary>100ms of 48kHz stereo, the largest block we ever hand the engine at once.</summary>
    private const int ScratchSamples = AudioFormat.SampleRate * AudioFormat.Channels / 10;

    /// <summary>
    /// Ceiling on pulls per capture callback. Measured worst case across 8000Hz to 96000Hz
    /// and 1ms to 200ms blocks is five, so this only ever fires if a provider misbehaves.
    /// It exists because this loop runs on the WASAPI capture thread, where spinning would
    /// wedge the microphone rather than just burning a core.
    /// </summary>
    private const int MaxPullsPerBlock = 32;

    /// <summary>Time constant of the meter fall. Roughly 98% down after one second.</summary>
    private const float PeakDecaySeconds = 0.25f;

    private readonly Lock Gate = new();
    private readonly Lock FailureGate = new();
    private readonly List<MicChannel> _channels = [];
    private readonly List<(string FriendlyName, string Error)> _failures = [];
    private readonly DeviceService _devices;
    private readonly AudioEngine _engine;

    public MicCaptureService(DeviceService devices, AudioEngine engine)
    {
        _devices = devices;
        _engine = engine;
    }

    /// <summary>
    /// Microphones that could not be opened, or that stopped on their own, with the reason.
    ///
    /// Surfaced in the UI rather than swallowed. A mic that silently never opens looks
    /// exactly like a mic that is working but muted, and the user has no way to tell.
    /// </summary>
    public IReadOnlyList<(string FriendlyName, string Error)> Failures
    {
        get
        {
            lock (FailureGate)
            {
                return _failures.ToList();
            }
        }
    }

    /// <summary>
    /// Open a WASAPI capture stream per enabled input device.
    /// Safe to call repeatedly; the previous set is torn down first.
    /// </summary>
    public void Start(IEnumerable<AudioDeviceRef> inputs)
    {
        lock (Gate)
        {
            StopLocked();

            var failures = new List<(string, string)>();
            var opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reference in inputs.Where(i => i.Enabled))
            {
                MMDevice? device = null;
                try
                {
                    device = _devices.Resolve(reference, DataFlow.Capture);
                    if (device is null)
                    {
                        failures.Add((reference.FriendlyName, "Microphone not found. It may be unplugged or disabled."));
                        continue;
                    }

                    // Resolve falls back to a friendly-name match when the saved id is gone,
                    // so a stale reference can land on a render endpoint. Capturing one would
                    // record the speakers and feed them straight back into the mix.
                    if (device.DataFlow != DataFlow.Capture)
                    {
                        failures.Add((reference.FriendlyName, "That endpoint is an output, not a microphone."));
                        continue;
                    }

                    // The same mic listed twice would push every block twice and double the voice.
                    if (!opened.Add(device.ID)) continue;

                    _channels.Add(OpenChannel(device, reference));
                    device = null; // ownership passed to the channel
                }
                catch (Exception ex)
                {
                    failures.Add((reference.FriendlyName, Describe(ex)));
                }
                finally
                {
                    device?.Dispose();
                }
            }

            lock (FailureGate)
            {
                _failures.Clear();
                _failures.AddRange(failures);
            }
        }
    }

    public void Stop()
    {
        lock (Gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        foreach (var channel in _channels) channel.Dispose();
        _channels.Clear();
    }

    private MicChannel OpenChannel(MMDevice device, AudioDeviceRef reference)
    {
        // Event sync lets WASAPI wake the capture thread instead of it polling on a sleep,
        // which keeps voice latency near the buffer length rather than roughly double it.
#pragma warning disable CS0618 // See the note on MicChannel.Capture.
        var capture = new WasapiCapture(device, true, CaptureBufferMs);
#pragma warning restore CS0618

        try
        {
            // WasapiCapture.WaveFormat unwraps WAVE_FORMAT_EXTENSIBLE into a plain
            // WAVEFORMATEX on the way out, so Encoding and BitsPerSample below are the
            // real ones rather than the Extensible tag that would match nothing.
            var format = capture.WaveFormat;

            var source = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(SourceBufferMs))
            {
                // Must stay false. With ReadFully on, this provider manufactures silence
                // forever once drained and the pull loop below would never finish a callback.
                ReadFully = false,
                // A stalled reader must drop audio rather than throw on the capture thread.
                DiscardOnBufferOverflow = true,
            };

            var chain = BuildChain(source)
                ?? throw new NotSupportedException(
                    $"Unsupported capture format: {format.Encoding} {format.BitsPerSample} bit.");

            var channel = new MicChannel
            {
                DeviceId = device.ID,
                FriendlyName = reference.FriendlyName,
                Device = device,
                Capture = capture,
                Source = source,
                Chain = chain,
                SourceBlockAlign = format.BlockAlign,
                RateRatio = (double)AudioFormat.SampleRate / format.SampleRate,
                Scratch = new float[ScratchSamples],
                Volume = Math.Clamp(reference.Volume, 0f, 1f),
                PeakStamp = Stopwatch.GetTimestamp(),
            };

            capture.DataAvailable += (_, e) => OnDataAvailable(channel, e);
            capture.RecordingStopped += (_, e) => OnRecordingStopped(channel, e);
            capture.StartRecording();

            return channel;
        }
        catch (Exception)
        {
            // A half-opened capture still holds the microphone, which blocks other apps.
            try { capture.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// Build the conversion from one microphone's capture format to <see cref="AudioFormat.Mix"/>.
    /// Returns null for a format we cannot read, which the caller turns into a named failure
    /// rather than an exception on the capture thread.
    /// </summary>
    private static ISampleProvider? BuildChain(BufferedWaveProvider source)
    {
        var samples = ToFloatSamples(source);
        if (samples is null) return null;

        if (samples.WaveFormat.Channels == 1)
        {
            samples = new MonoToStereoSampleProvider(samples);
        }
        else if (samples.WaveFormat.Channels > AudioFormat.Channels)
        {
            // Array microphones and some webcams report four or more channels. Take the
            // first pair rather than summing them, because the extra channels are usually
            // beamforming residue and summing them smears the voice.
            var pick = new MultiplexingSampleProvider([samples], AudioFormat.Channels);
            pick.ConnectInputToOutput(0, 0);
            pick.ConnectInputToOutput(1, 1);
            samples = pick;
        }

        if (samples.WaveFormat.SampleRate != AudioFormat.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, AudioFormat.SampleRate);

        return samples;
    }

    /// <summary>
    /// A shared-mode capture is whatever the endpoint's mix format happens to be, which in
    /// practice is 32 bit float but is 16 bit PCM on plenty of USB and Bluetooth headsets.
    /// Both are handled explicitly so an exotic format fails by name instead of by exception.
    /// </summary>
    private static ISampleProvider? ToFloatSamples(IWaveProvider source)
    {
        var format = source.WaveFormat;
        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => new WaveToSampleProvider(source),
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 64 => new WaveToSampleProvider64(source),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 8 => new Pcm8BitToSampleProvider(source),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => new Pcm16BitToSampleProvider(source),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => new Pcm24BitToSampleProvider(source),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => new Pcm32BitToSampleProvider(source),
            _ => null,
        };
    }

    private void OnDataAvailable(MicChannel channel, WaveInEventArgs e)
    {
        // WasapiCapture fires an empty block at the end of every packet loop, so most
        // callbacks on a quiet device carry nothing.
        if (channel.Stopping || e.BytesRecorded <= 0) return;

        try
        {
            channel.Source.AddSamples(e.BufferSpan);
            Drain(channel);
        }
        catch (Exception ex)
        {
            // One bad block must not kill the capture thread and take the microphone with it.
            RecordFailure(channel.FriendlyName, Describe(ex));
        }
    }

    /// <summary>
    /// Pull everything the buffered input can currently produce and push it to the engine.
    /// </summary>
    private void Drain(MicChannel channel)
    {
        for (var pull = 0; pull < MaxPullsPerBlock; pull++)
        {
            // Ask for only as much output as the buffered input can actually fill.
            //
            // This is the whole trick, and getting it wrong is silent. WdlResamplingSampleProvider
            // sizes its demand on the source from the length of the span you hand it, so asking
            // for 100ms of output when 10ms of input has arrived makes it swallow that input and
            // return zero. Measured on a 44100 to 48000 conversion with 10ms blocks, a fixed-size
            // pull delivered 1% of the audio and stranded the rest inside the resampler; sizing
            // the pull by the rate ratio delivers it all, within 0.4ms over a second.
            var pendingFrames = channel.Source.BufferedBytes / channel.SourceBlockAlign;
            var wanted = (int)(pendingFrames * channel.RateRatio) * AudioFormat.Channels;
            if (wanted <= 0) return;
            if (wanted > channel.Scratch.Length) wanted = channel.Scratch.Length;

            var read = channel.Chain.Read(channel.Scratch.AsSpan(0, wanted));
            if (read <= 0) return;

            Emit(channel, channel.Scratch.AsSpan(0, read));
        }
    }

    private void Emit(MicChannel channel, Span<float> samples)
    {
        var gain = channel.Volume;
        var peak = 0f;

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i] * gain;
            samples[i] = value;

            var magnitude = MathF.Abs(value);
            if (magnitude > peak) peak = magnitude;
        }

        // These floats are already little-endian 32 bit, which is exactly the byte layout
        // AudioFormat.Mix describes, so this reinterprets the span rather than copying it.
        _engine.PushMicSamples(MemoryMarshal.AsBytes(samples));

        lock (channel.PeakGate)
        {
            if (peak > channel.Peak)
            {
                channel.Peak = peak;
                channel.PeakStamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private void OnRecordingStopped(MicChannel channel, StoppedEventArgs e)
    {
        // When no SynchronizationContext was captured this runs on the capture thread
        // itself, before that thread exits and therefore before a disposing thread's join
        // returns. It must not touch Gate. See MicChannel.PeakGate.
        if (channel.Stopping || e.Exception is null) return;
        RecordFailure(channel.FriendlyName, Describe(e.Exception));
    }

    private void RecordFailure(string friendlyName, string error)
    {
        lock (FailureGate)
        {
            // A device failing on every block would otherwise grow this list without limit.
            if (_failures.Any(f => f.FriendlyName == friendlyName && f.Error == error)) return;
            _failures.Add((friendlyName, error));
        }
    }

    /// <summary>Live input levels per device, for the meters in the device panel.</summary>
    public IReadOnlyList<(string DeviceId, float Peak)> ReadInputPeaks()
    {
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            var peaks = new List<(string, float)>(_channels.Count);

            foreach (var channel in _channels)
            {
                float value;
                lock (channel.PeakGate)
                {
                    // Decay by elapsed time rather than per call, so the meter falls at the
                    // same speed whatever rate the UI happens to poll at.
                    var elapsed = (now - channel.PeakStamp) / (float)Stopwatch.Frequency;
                    if (elapsed > 0f) channel.Peak *= MathF.Exp(-elapsed / PeakDecaySeconds);
                    channel.PeakStamp = now;
                    value = channel.Peak;
                }

                // Resampling a loud passage can ring slightly past full scale, so the meter
                // is clamped even though the audio itself is left alone.
                peaks.Add((channel.DeviceId, Math.Clamp(value, 0f, 1f)));
            }

            return peaks;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        // HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED). On a capture endpoint this is almost
        // always the Windows microphone privacy switch rather than a real permissions problem,
        // and it is worth naming because nothing else in the app can hint at it.
        COMException { HResult: unchecked((int)0x80070005) } =>
            "Windows blocked access to this microphone. Check Settings, Privacy and security, Microphone.",
        COMException com =>
            $"Windows refused the microphone (0x{com.HResult:X8}). Another app may hold it in exclusive mode.",
        _ => ex.Message,
    };

    public void Dispose() => Stop();
}
