using NAudio.Wave;

namespace Patchboard.Services;

/// <summary>
/// The one internal format everything is converted to before it reaches a mixer.
///
/// Having a single canonical format is what makes multi-device output tractable: every
/// mixer, every cached sound and the microphone all speak 48kHz stereo float, so the
/// only place a conversion can be needed is the final hop into a device that runs at a
/// different rate. 48kHz is the Windows shared-mode default and what VB-Cable and
/// Voicemeeter run at, so in practice that final conversion is usually a no-op.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public static readonly WaveFormat Mix =
        WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
}
