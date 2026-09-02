using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Patchboard.Services;

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
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);

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
    public static CachedSound Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Sound file is missing: {filePath}", filePath);

        using var reader = new AudioFileReader(filePath);

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

        var samples = new List<float>((int)Math.Min(maxSamples, 1 << 22));
        var buffer = new float[AudioFormat.SampleRate * AudioFormat.Channels]; // 1 second
        int read;
        while ((read = source.Read(buffer.AsSpan())) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
            if (samples.Count > maxSamples)
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(filePath)}' is longer than {MaxDuration.TotalMinutes:0} minutes. " +
                    "Trim it or use a shorter clip.");
        }

        if (samples.Count == 0)
            throw new InvalidOperationException($"'{Path.GetFileName(filePath)}' decoded to zero audio.");

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
