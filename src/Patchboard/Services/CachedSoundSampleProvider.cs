using NAudio.Wave;

namespace Patchboard.Services;

/// <summary>
/// One playing instance of a <see cref="CachedSound"/> on one output device.
///
/// Holds only a position and a volume, so a single decoded clip can be playing on the
/// headphones, the virtual cable and the game mic at once with three of these, each a
/// few dozen bytes. Returning 0 from <see cref="Read"/> makes NAudio's mixer drop it,
/// which is how finished sounds clean themselves up.
/// </summary>
public sealed class CachedSoundSampleProvider : ISampleProvider
{
    /// <summary>
    /// Stopping dead on a non-zero sample makes an audible click, which on a soundboard
    /// happens every time you hit stop-all. A short ramp removes it.
    /// </summary>
    private const int FadeOutSamples = AudioFormat.SampleRate * AudioFormat.Channels / 66; // ~15ms

    private readonly CachedSound _sound;
    private long _position;
    private int _fadeRemaining = -1;

    public CachedSoundSampleProvider(CachedSound sound, float volume)
    {
        _sound = sound;
        Volume = volume;
    }

    public WaveFormat WaveFormat => AudioFormat.Mix;

    /// <summary>Live gain for this instance. Safe to set from the UI thread while playing.</summary>
    public volatile float Volume;

    /// <summary>True once the clip has run out or a stop fade has completed.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>How far through the clip we are, for the progress line on the button.</summary>
    public double Progress =>
        _sound.AudioData.Length == 0 ? 0 : (double)_position / _sound.AudioData.Length;

    /// <summary>Begin a short fade and then end. Never cuts the buffer dead.</summary>
    public void RequestStop()
    {
        if (_fadeRemaining < 0) _fadeRemaining = FadeOutSamples;
    }

    public int Read(Span<float> buffer)
    {
        if (IsFinished) return 0;

        var available = _sound.AudioData.Length - _position;
        var count = (int)Math.Min(buffer.Length, available);
        if (count <= 0)
        {
            IsFinished = true;
            return 0;
        }

        var source = _sound.AudioData.AsSpan((int)_position, count);
        var gain = Volume;

        if (_fadeRemaining < 0)
        {
            for (var i = 0; i < count; i++)
                buffer[i] = source[i] * gain;
        }
        else
        {
            // Linear ramp to silence, then stop for good.
            for (var i = 0; i < count; i++)
            {
                if (_fadeRemaining <= 0)
                {
                    _position += i;
                    IsFinished = true;
                    return i;
                }
                var fade = (float)_fadeRemaining / FadeOutSamples;
                buffer[i] = source[i] * gain * fade;
                _fadeRemaining--;
            }
        }

        _position += count;
        if (_position >= _sound.AudioData.Length) IsFinished = true;
        return count;
    }
}
