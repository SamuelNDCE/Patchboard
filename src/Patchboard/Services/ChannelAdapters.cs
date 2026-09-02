using NAudio.Wave;

namespace Patchboard.Services;

/// <summary>
/// Places a stereo signal into the first two channels of a wider endpoint and leaves the
/// rest silent.
///
/// This exists for HDMI and surround endpoints, which report a mix format of six or eight
/// channels. Handing such a device a stereo stream makes WASAPI refuse to initialise, and
/// the symptom is one output in the list silently never playing while the others work.
/// </summary>
internal sealed class StereoToMultiChannelSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _targetChannels;
    private float[] _buffer = [];

    public StereoToMultiChannelSampleProvider(ISampleProvider source, int targetChannels)
    {
        if (source.WaveFormat.Channels != 2)
            throw new ArgumentException("Source must be stereo.", nameof(source));

        _source = source;
        _targetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / _targetChannels;
        var needed = frames * 2;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = _source.Read(_buffer.AsSpan(0, needed));
        var framesRead = read / 2;

        buffer[..(framesRead * _targetChannels)].Clear();
        for (var f = 0; f < framesRead; f++)
        {
            buffer[f * _targetChannels] = _buffer[f * 2];
            buffer[f * _targetChannels + 1] = _buffer[f * 2 + 1];
        }

        return framesRead * _targetChannels;
    }
}
