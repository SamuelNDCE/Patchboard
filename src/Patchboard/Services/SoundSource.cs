using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Patchboard.Services;

/// <summary>
/// One playing copy of a sound on one output device.
///
/// The engine holds several of these per press, one per device, and treats them
/// identically whether the audio came from memory or is being read off the disk as it
/// plays. That is the whole reason this interface exists: a clip short enough to hold in
/// RAM and an hour long recording that could never fit have to behave the same to the
/// mixer, to the progress line, and to a drag on the seek bar.
/// </summary>
public interface IPlaybackInstance : ISampleProvider, IDisposable
{
    /// <summary>Live gain for this copy. Safe to set while it plays.</summary>
    float Volume { get; set; }

    /// <summary>True once the audio has run out or a stop fade has completed.</summary>
    bool IsFinished { get; }

    /// <summary>0 to 1 through the clip.</summary>
    double Progress { get; }

    /// <summary>Begin a short fade and then end. Never cuts the buffer dead.</summary>
    void RequestStop();

    /// <summary>Jump to a fraction of the way through, 0 to 1.</summary>
    void Seek(double fraction);
}

/// <summary>
/// Something a button can play. Hands out one <see cref="IPlaybackInstance"/> per device.
/// </summary>
public interface ISoundSource
{
    /// <summary>Length of the audio this source plays, after any trim.</summary>
    TimeSpan Duration { get; }

    /// <summary>True when the audio is held in memory rather than read from disk.</summary>
    bool IsResident { get; }

    IPlaybackInstance CreateInstance(float volume);
}

/// <summary>A clip decoded into memory. Instances are a few dozen bytes each.</summary>
public sealed class CachedSoundSource(CachedSound sound) : ISoundSource
{
    public CachedSound Sound { get; } = sound;

    public TimeSpan Duration => Sound.Duration;

    public bool IsResident => true;

    public IPlaybackInstance CreateInstance(float volume) => new CachedSoundSampleProvider(Sound, volume);
}

/// <summary>
/// A clip read from disk as it plays, rather than decoded into memory first.
///
/// This is what lets a recording of any length go on a button. Decoding an hour of audio
/// to 48kHz stereo float is roughly 700MB, which is not something a soundboard should hold
/// so that one button can work, and the cache would evict it immediately anyway because it
/// is larger than the whole budget, so every press would decode it again from scratch.
///
/// The cost is a file handle and a decoder per device while it plays, and the read happens
/// on the render thread. For one long clip across two or three devices that is nothing; it
/// would matter if every button worked this way, which is why short clips still do not.
/// </summary>
public sealed class StreamingSoundSource(string filePath, TimeSpan duration, int startMs, int endMs)
    : ISoundSource
{
    public string FilePath { get; } = filePath;

    public TimeSpan Duration { get; } = duration;

    public bool IsResident => false;

    public IPlaybackInstance CreateInstance(float volume) =>
        new StreamingSampleProvider(FilePath, startMs, endMs, volume);

    /// <summary>
    /// Open the file just far enough to learn its length and confirm it can be decoded.
    /// Throws the same exceptions as <see cref="CachedSound.Load"/> so a button reports a
    /// streamed failure exactly the way it reports a decoded one.
    /// </summary>
    public static StreamingSoundSource Open(string filePath, int startMs, int endMs)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Sound file is missing: {filePath}", filePath);

        using var reader = new AudioFileReader(filePath);
        var window = TrimWindow.Resolve(reader.TotalTime, startMs, endMs);

        if (window.Length <= TimeSpan.Zero)
            throw new ClipEmptyException(Path.GetFileName(filePath));

        return new StreamingSoundSource(filePath, window.Length, startMs, endMs);
    }
}

/// <summary>
/// Resolving a start and an end against a file's real duration.
///
/// Shared so the decoded path and the streamed path cannot disagree about what a trim
/// means. A nonsense window, from a hand edited config or a file that has since been
/// replaced by a shorter one, falls back rather than producing silence.
/// </summary>
public readonly record struct TrimWindow(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;

    public static TrimWindow Resolve(TimeSpan total, int startMs, int endMs)
    {
        var start = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        if (start >= total) start = TimeSpan.Zero;

        var end = endMs > 0 ? TimeSpan.FromMilliseconds(endMs) : total;
        if (end > total || end <= start) end = total;

        return new TrimWindow(start, end);
    }
}

/// <summary>
/// Reads one clip off the disk as it plays, in <see cref="AudioFormat.Mix"/>.
///
/// Owns its own reader and conversion chain, so several devices playing the same file each
/// have their own and cannot pull each other's audio away. Not thread safe, and does not
/// need to be: exactly one WASAPI render thread reads each instance.
/// </summary>
internal sealed class StreamingSampleProvider : IPlaybackInstance
{
    private const int FadeOutSamples = AudioFormat.SampleRate * AudioFormat.Channels / 66; // ~15ms

    private readonly AudioFileReader _reader;
    private readonly ISampleProvider _chain;
    private readonly TrimWindow _window;
    private readonly long _totalSamples;

    private long _position;
    private int _fadeRemaining = -1;
    private bool _disposed;

    public StreamingSampleProvider(string filePath, int startMs, int endMs, float volume)
    {
        _reader = new AudioFileReader(filePath);
        Volume = volume;

        _window = TrimWindow.Resolve(_reader.TotalTime, startMs, endMs);
        _totalSamples = (long)(_window.Length.TotalSeconds * AudioFormat.SampleRate) * AudioFormat.Channels;

        if (_window.Start > TimeSpan.Zero) _reader.CurrentTime = _window.Start;

        _chain = BuildChain(_reader);
    }

    /// <summary>
    /// Channel fold then resample, the same order and the same components the decoded path
    /// uses, so a streamed clip and a decoded one sound identical.
    /// </summary>
    internal static ISampleProvider BuildChain(AudioFileReader reader)
    {
        ISampleProvider source = reader.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(reader),
            2 => reader,
            _ => new ChannelFoldSampleProvider(reader),
        };

        return source.WaveFormat.SampleRate != AudioFormat.SampleRate
            ? new WdlResamplingSampleProvider(source, AudioFormat.SampleRate)
            : source;
    }

    public WaveFormat WaveFormat => AudioFormat.Mix;

    public float Volume { get; set; }

    public bool IsFinished { get; private set; }

    public double Progress => _totalSamples == 0 ? 0 : Math.Clamp((double)_position / _totalSamples, 0, 1);

    public void RequestStop()
    {
        if (_fadeRemaining < 0) _fadeRemaining = FadeOutSamples;
    }

    public void Seek(double fraction)
    {
        if (_disposed || IsFinished) return;

        var target = _window.Start + _window.Length * Math.Clamp(fraction, 0, 1);

        try
        {
            _reader.CurrentTime = target;
        }
        catch (Exception)
        {
            // A format that refuses to seek keeps playing from where it was rather than
            // ending the sound. Losing a seek is a nuisance; losing the clip is not.
            return;
        }

        _position = (long)((target - _window.Start).TotalSeconds * AudioFormat.SampleRate) * AudioFormat.Channels;
    }

    public int Read(Span<float> buffer)
    {
        if (_disposed || IsFinished) return 0;

        // Never read past the trim's end, however much the caller asked for.
        var remaining = _totalSamples - _position;
        if (remaining <= 0)
        {
            IsFinished = true;
            return 0;
        }

        var want = (int)Math.Min(buffer.Length, remaining);
        int read;
        try
        {
            read = _chain.Read(buffer[..want]);
        }
        catch (Exception)
        {
            // The file can be deleted or a drive unplugged mid play. End the sound rather
            // than throwing on the render thread, which would take the device down.
            IsFinished = true;
            return 0;
        }

        if (read <= 0)
        {
            IsFinished = true;
            return 0;
        }

        var gain = Volume;

        if (_fadeRemaining < 0)
        {
            for (var i = 0; i < read; i++) buffer[i] *= gain;
        }
        else
        {
            for (var i = 0; i < read; i++)
            {
                if (_fadeRemaining <= 0)
                {
                    _position += i;
                    IsFinished = true;
                    return i;
                }

                buffer[i] *= gain * ((float)_fadeRemaining / FadeOutSamples);
                _fadeRemaining--;
            }
        }

        _position += read;
        if (_position >= _totalSamples) IsFinished = true;
        return read;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsFinished = true;
        try { _reader.Dispose(); } catch (Exception) { }
    }
}
