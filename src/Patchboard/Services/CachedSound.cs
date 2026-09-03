using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Patchboard.Services;

/// <summary>
/// A clip that is longer than the decoder will accept. A distinct type rather than a
/// message the UI has to string match, because the label on the button depends on telling
/// this apart from a file that is genuinely gone.
/// </summary>
public sealed class ClipTooLongException(string fileName, TimeSpan length, TimeSpan limit)
    : InvalidOperationException(
        $"'{fileName}' is {length.TotalMinutes:0} minutes long. The limit is " +
        $"{limit.TotalMinutes:0} minutes, so trim it or use a shorter clip.");

/// <summary>A file that opened and produced no audio at all.</summary>
public sealed class ClipEmptyException(string fileName)
    : InvalidOperationException($"'{fileName}' decoded to zero audio.");

/// <summary>
/// A sound file decoded once into memory, in <see cref="AudioFormat.Mix"/>.
///
/// This is the core of how one clip reaches several devices at once. Decoding happens a
/// single time; each output device then gets its own cheap <see cref="CachedSoundSampleProvider"/>
/// reading the same shared array. The alternative, one AudioFileReader per device, would
/// re-read and re-decode the file N times and let the outputs drift apart.
/// </summary>
public sealed class CachedSound
{
    /// <summary>Refuse anything longer than this rather than exhaust memory. 20 min is ~230MB.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);

    public float[] AudioData { get; }
    public string FilePath { get; }
    public TimeSpan Duration { get; }

    private CachedSound(string filePath, float[] data)
    {
        FilePath = filePath;
        AudioData = data;
        Duration = TimeSpan.FromSeconds(
            (double)data.Length / AudioFormat.Channels / AudioFormat.SampleRate);
    }

    /// <summary>
    /// Decode <paramref name="filePath"/> to the mix format. Throws on unreadable or
    /// oversized files; callers surface the message rather than crashing the app.
    /// </summary>
    /// <param name="startMs">Where to begin, in milliseconds. 0 is the start of the file.</param>
    /// <param name="endMs">Where to stop, in milliseconds. 0 means the end of the file.</param>
    public static CachedSound Load(string filePath, int startMs = 0, int endMs = 0)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Sound file is missing: {filePath}", filePath);

        using var reader = new AudioFileReader(filePath);

        // Ask how long it is BEFORE decoding any of it.
        //
        // Without this the length check below only fires once enough samples have been
        // decoded to exceed the cap, so an over-long clip was decoded for a full twenty
        // minutes of audio and only then rejected. Measured from the real library: pressing
        // a 71 minute button blocked for 4.4 seconds and then reported a failure, and a 49
        // minute one for 3.0 seconds. Reading TotalTime turns both into a few milliseconds.
        var total = reader.TotalTime;

        // Resolve the trim against the real duration. A nonsense window, from a hand
        // edited config or a file that has since been replaced by a shorter one, falls
        // back to the whole file rather than producing silence.
        var start = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        if (start >= total) start = TimeSpan.Zero;

        var end = endMs > 0 ? TimeSpan.FromMilliseconds(endMs) : total;
        if (end > total || end <= start) end = total;

        // The limit applies to the piece being kept, not to the file it came from, which
        // is the entire point: an hour long recording trimmed to eight seconds is eight
        // seconds of audio and there is no reason to refuse it.
        var length = end - start;
        if (length > MaxDuration)
            throw new ClipTooLongException(Path.GetFileName(filePath), length, MaxDuration);

        if (start > TimeSpan.Zero) reader.CurrentTime = start;

        // Channel fold first, then resample. Doing it the other way round would make the
        // resampler do more work than necessary on surround content.
        ISampleProvider source = reader.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(reader),
            2 => reader,
            _ => new ChannelFoldSampleProvider(reader),
        };

        if (source.WaveFormat.SampleRate != AudioFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, AudioFormat.SampleRate);

        var maxSamples = (long)MaxDuration.TotalSeconds * AudioFormat.SampleRate * AudioFormat.Channels;

        // Only the kept window is decoded. Reading the whole file and slicing afterwards
        // would put back the cost that trimming exists to remove.
        //
        // The stopping rule differs by case, and the distinction matters. A TRIMMED clip
        // stops at the requested length, because the user named that length. An UNTRIMMED
        // one reads to end of stream and is bounded only by the hard cap: a VBR MP3 with no
        // Xing header under-reports its own duration, and stopping at what the header
        // claimed would silently cut the end off a clip that was perfectly fine before.
        var trimmed = startMs > 0 || endMs > 0;
        var limit = trimmed
            ? (long)(length.TotalSeconds * AudioFormat.SampleRate) * AudioFormat.Channels
            : maxSamples;

        var samples = new List<float>((int)Math.Min(limit, 1 << 22));
        var buffer = new float[AudioFormat.SampleRate * AudioFormat.Channels]; // 1 second
        int read;
        while (samples.Count < limit && (read = source.Read(buffer.AsSpan())) > 0)
        {
            var take = (int)Math.Min(read, limit - samples.Count);
            samples.AddRange(buffer.AsSpan(0, take));
        }

        // Reaching the cap on an untrimmed clip means the header lied about the duration
        // and the file really is too long. This is the backstop the TotalTime check cannot
        // provide, and it is why the cap is a stopping condition rather than an assertion.
        if (!trimmed && samples.Count >= maxSamples)
            throw new ClipTooLongException(Path.GetFileName(filePath), MaxDuration, MaxDuration);

        if (samples.Count == 0)
            throw new ClipEmptyException(Path.GetFileName(filePath));

        return new CachedSound(filePath, samples.ToArray());
    }
}

/// <summary>
/// Folds any channel count down to stereo by averaging odd channels into the left and
/// even into the right. Crude next to a proper ITU downmix, but this only ever runs on
/// surround source files, which are vanishingly rare in a soundboard.
/// </summary>
internal sealed class ChannelFoldSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _buffer = [];

    public ChannelFoldSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        var needed = frames * _sourceChannels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = _source.Read(_buffer.AsSpan(0, needed));
        var framesRead = read / _sourceChannels;

        for (var f = 0; f < framesRead; f++)
        {
            float left = 0, right = 0;
            int leftCount = 0, rightCount = 0;
            for (var c = 0; c < _sourceChannels; c++)
            {
                var sample = _buffer[f * _sourceChannels + c];
                if (c % 2 == 0) { left += sample; leftCount++; }
                else { right += sample; rightCount++; }
            }
            buffer[f * 2] = leftCount > 0 ? left / leftCount : 0f;
            buffer[f * 2 + 1] = rightCount > 0 ? right / rightCount : 0f;
        }

        return framesRead * 2;
    }
}
