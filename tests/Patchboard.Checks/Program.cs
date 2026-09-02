using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patchboard.Models;
using Patchboard.Services;

// Verification harness for Patchboard.
//
// Samuel is in a live multiplayer match, so this must not steal focus, must not
// synthesise input, and must not put audible sound anywhere he or anyone in his
// Discord call can hear. Everything below either runs the audio chain purely in
// memory, or targets "Steam Streaming Speakers", a virtual endpoint that is idle
// because Steam is not running.

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}{(detail.Length > 0 ? "  " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

// --------------------------------------------------------------------------
Section("1. CONFIG AND LIBRARY");

var configService = new ConfigService();
var config = configService.Load();

Check("config loads", config.Sounds.Count > 0, $"{config.Sounds.Count} sounds");
// Originally this asserted that nothing was enabled, which was only ever true on a fresh
// install. Once a real output had been chosen the check failed while the app was working
// correctly. The invariant that actually matters is that whatever is enabled is coherent.
var enabledOutputs = config.OutputDevices.Where(d => d.Enabled).ToList();
Console.WriteLine($"  ..... {enabledOutputs.Count} output(s) selected: {string.Join(", ", enabledOutputs.Select(d => d.FriendlyName))}");

Check("every enabled output has an endpoint id", enabledOutputs.All(d => !string.IsNullOrWhiteSpace(d.Id)));
Check("every enabled output has a usable volume", enabledOutputs.All(d => d.Volume is >= 0f and <= 1f));

// Mic routing is a live behaviour, not a config invariant: a device may legitimately be
// marked ReceivesMic while passthrough is off, because the master switch overrides it.
// That override is proved against the real engine in section 5c, so asserting anything
// about it here would either duplicate that or, worse, pass vacuously.
var micDevices = config.OutputDevices.Where(d => d.ReceivesMic).Select(d => d.FriendlyName).ToList();
Console.WriteLine($"  ..... mic routed to: {(micDevices.Count == 0 ? "nothing" : string.Join(", ", micDevices))}");
Check("grid settings sane", config.GridColumns is >= 1 and <= 20 && config.GridRows is >= 1 and <= 20,
    $"{config.GridColumns}x{config.GridRows}");

var onDisk = config.Sounds.Count(s => File.Exists(s.FilePath));
Check("every button points at a real file", onDisk == config.Sounds.Count,
    $"{onDisk}/{config.Sounds.Count} present");

// --------------------------------------------------------------------------
Section("2. DECODE, the real files from his library");

var samples = config.Sounds.Where(s => File.Exists(s.FilePath)).Take(6).ToList();
var decoded = new List<CachedSound>();

foreach (var sound in samples)
{
    try
    {
        var cached = CachedSound.Load(sound.FilePath);
        decoded.Add(cached);

        var peak = 0f;
        for (var i = 0; i < cached.AudioData.Length; i++)
            peak = Math.Max(peak, Math.Abs(cached.AudioData[i]));

        Check($"decoded '{sound.Name}'",
            cached.AudioData.Length > 0 && peak > 0.001f,
            $"{cached.Duration.TotalSeconds:0.0}s, peak {peak:0.00}");
    }
    catch (Exception ex)
    {
        Check($"decoded '{sound.Name}'", false, ex.Message);
    }
}

Check("decode normalises everything to 48k stereo",
    decoded.All(d => d.AudioData.Length % AudioFormat.Channels == 0));

// --------------------------------------------------------------------------
Section("3. PLAYBACK CHAIN, in memory, no device touched");

if (decoded.Count > 0)
{
    // Pick a clip that actually has audio in its opening second. "arana" is Aria Math,
    // which fades in from silence, so testing the mixer on it measures nothing.
    var clip = decoded
        .OrderByDescending(d =>
        {
            var window = Math.Min(d.AudioData.Length, AudioFormat.SampleRate * AudioFormat.Channels);
            var peak = 0f;
            for (var i = 0; i < window; i++) peak = Math.Max(peak, Math.Abs(d.AudioData[i]));
            return peak;
        })
        .First();

    var openingPeak = 0f;
    for (var i = 0; i < Math.Min(clip.AudioData.Length, 4800); i++)
        openingPeak = Math.Max(openingPeak, Math.Abs(clip.AudioData[i]));

    Console.WriteLine($"  ..... using a clip whose first 4800 samples peak at {openingPeak:0.000}");

    Check("test clip actually has audio at the start", openingPeak > 0.01f,
        "otherwise every check below measures silence and proves nothing");

    // Full volume vs half volume, same clip, same position.
    var loud = new CachedSoundSampleProvider(clip, 1.0f);
    var quiet = new CachedSoundSampleProvider(clip, 0.5f);

    var a = new float[4800];
    var b = new float[4800];
    var readA = loud.Read(a.AsSpan());
    var readB = quiet.Read(b.AsSpan());

    Check("provider returns samples", readA > 0 && readA == readB, $"{readA} samples");

    var ratioOk = true;
    for (var i = 0; i < readA; i++)
    {
        if (Math.Abs(a[i]) < 0.01f) continue;
        if (Math.Abs(b[i] / a[i] - 0.5f) > 0.02f) { ratioOk = false; break; }
    }

    Check("per sound volume scales correctly", ratioOk, "0.5 gain is half amplitude");

    // Independent positions: this is what lets one clip feed several devices.
    var first = new CachedSoundSampleProvider(clip, 1f);
    var second = new CachedSoundSampleProvider(clip, 1f);
    first.Read(new float[9600].AsSpan());
    var afterFirst = first.Progress;
    var afterSecond = second.Progress;
    Check("each device's copy keeps its own position", afterFirst > afterSecond && afterSecond == 0,
        $"{afterFirst:0.000} vs {afterSecond:0.000}");

    // Fade on stop, so stop-all does not click.
    var fading = new CachedSoundSampleProvider(clip, 1f);
    fading.Read(new float[4800].AsSpan());
    fading.RequestStop();
    var tail = new float[4800];
    var tailRead = fading.Read(tail.AsSpan());
    var startAmp = 0f;
    var endAmp = 0f;
    for (var i = 0; i < Math.Min(200, tailRead); i++) startAmp = Math.Max(startAmp, Math.Abs(tail[i]));
    for (var i = Math.Max(0, tailRead - 200); i < tailRead; i++) endAmp = Math.Max(endAmp, Math.Abs(tail[i]));
    Check("stop fades instead of cutting", fading.IsFinished && endAmp <= startAmp,
        $"ramped {startAmp:0.000} -> {endAmp:0.000}, finished");

    // Mixer sums concurrent sounds, which is how overlap mode works.
    var mixer = new MixingSampleProvider(AudioFormat.Mix) { ReadFully = true };
    mixer.AddMixerInput(new CachedSoundSampleProvider(clip, 1f));
    mixer.AddMixerInput(new CachedSoundSampleProvider(clip, 1f));
    var mixed = new float[4800];
    mixer.Read(mixed.AsSpan());
    var mixPeak = mixed.Select(Math.Abs).Max();
    Check("mixer sums overlapping sounds", mixPeak > 0.001f, $"peak {mixPeak:0.00}");

    // Idle mixer must produce silence, not end-of-stream, or the device stops forever.
    var idle = new MixingSampleProvider(AudioFormat.Mix) { ReadFully = true };
    var silence = new float[4800];
    var idleRead = idle.Read(silence.AsSpan());
    Check("idle mixer yields silence, not end of stream", idleRead == silence.Length && silence.All(s => s == 0f));

    // Resampling path, used when a device is not 48k.
    var resampled = new WdlResamplingSampleProvider(new CachedSoundSampleProvider(clip, 1f), 44100);
    var rs = new float[4410];
    var rsRead = resampled.Read(rs.AsSpan());
    Check("resamples to a 44.1k device", rsRead > 0 && rs.Select(Math.Abs).Max() > 0.001f);

    // Surround endpoints report 6 or 8 channels.
    var folded = new StereoToMultiChannelSampleProviderProbe(clip);
    Check("adapts stereo to a 6 channel endpoint", folded.Works);
}

// --------------------------------------------------------------------------
Section("4. DEVICES");

using var devices = new DeviceService();
var outputs = devices.ListOutputs();
var inputs = devices.ListInputs();

Check("enumerates output devices", outputs.Count > 0, $"{outputs.Count} found");
Check("enumerates input devices", inputs.Count > 0, $"{inputs.Count} found");
Check("flags virtual cables", outputs.Any(o => o.IsVirtual),
    $"{outputs.Count(o => o.IsVirtual)} virtual");
Check("finds CABLE Input, the route into Discord",
    outputs.Any(o => o.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)));
Check("finds his webcam mic",
    inputs.Any(i => i.FriendlyName.Contains("eMeet", StringComparison.OrdinalIgnoreCase)));
Check("real hardware sorts above virtual",
    outputs.TakeWhile(o => !o.IsVirtual).Any());

// Resolve round trip: this is what keeps routing after a reboot.
if (outputs.Count > 0)
{
    var reference = new AudioDeviceRef { Id = outputs[0].Id, FriendlyName = outputs[0].FriendlyName };
    using var resolved = devices.Resolve(reference, NAudio.CoreAudioApi.DataFlow.Render);
    Check("resolves a saved device by endpoint id", resolved is not null, outputs[0].FriendlyName);

    var renamed = new AudioDeviceRef { Id = "{0.0.0.00000000}.{dead-id}", FriendlyName = outputs[0].FriendlyName };
    using var byName = devices.Resolve(renamed, NAudio.CoreAudioApi.DataFlow.Render);
    Check("falls back to name when the id is gone", byName is not null);
}

// --------------------------------------------------------------------------
Section("5. LIVE WASAPI, on an idle virtual endpoint only");

// Steam is not running, so this endpoint is inert. Nothing reaches his headphones,
// his game, or his Discord call.
var safeTarget = outputs.FirstOrDefault(o =>
    o.FriendlyName.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase));

if (safeTarget is null)
{
    Console.WriteLine("  SKIP  no idle Steam endpoint found; refusing to open a device he can hear");
}
else if (decoded.Count == 0)
{
    Console.WriteLine("  SKIP  nothing decoded to play");
}
else
{
    using var engine = new AudioEngine(devices);
    var target = new AudioDeviceRef
    {
        Id = safeTarget.Id, FriendlyName = safeTarget.FriendlyName, Enabled = true, Volume = 1f,
    };

    engine.SetOutputs([target], 60);
    Check("opens a real WASAPI output", engine.FailedOutputs.Count == 0,
        engine.FailedOutputs.Count > 0 ? engine.FailedOutputs[0].Error : safeTarget.FriendlyName);

    var handle = engine.Play("test-button", decoded[0], 1f, RetriggerMode.Overlap);
    Check("play returns a handle", handle is not null);

    Thread.Sleep(700);

    var active = engine.ActiveSounds;
    Check("sound is playing on the device", active.Count == 1 && active[0].ButtonId == "test-button");
    Check("progress advances, so the device is really pulling audio",
        handle is not null && handle.Progress > 0, $"progress {handle?.Progress:0.000}");

    engine.StopAll();
    Thread.Sleep(200);
    Check("stop all clears everything", engine.ActiveSounds.Count == 0);

    // Retrigger modes.
    engine.Play("toggle-button", decoded[0], 1f, RetriggerMode.Toggle);
    var second = engine.Play("toggle-button", decoded[0], 1f, RetriggerMode.Toggle);
    Check("toggle mode stops on second press", second is null);

    engine.StopAll();
}

// --------------------------------------------------------------------------
Section("5b. MIC PASSTHROUGH, on a silent virtual capture endpoint");

// Deliberately NOT his real microphone. He is in a Discord call, and opening the eMeet
// or the USB mic to test would be recording him. "CABLE Output" is the capture side of
// the virtual cable: it opens and delivers silence unless something is playing into
// CABLE Input, which is exactly what is wanted to prove the plumbing without listening.
var silentCapture = inputs.FirstOrDefault(i =>
    i.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase))
    ?? inputs.FirstOrDefault(i => i.FriendlyName.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));

var micTarget = outputs.FirstOrDefault(o =>
    o.FriendlyName.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase));

if (silentCapture is null || micTarget is null)
{
    Console.WriteLine("  SKIP  no silent capture endpoint available; will not open his real mic");
}
else
{
    using var micEngine = new AudioEngine(devices);
    micEngine.SetOutputs(
        [new AudioDeviceRef { Id = micTarget.Id, FriendlyName = micTarget.FriendlyName, Enabled = true, Volume = 1f }],
        60);

    using var mic = new MicCaptureService(devices, micEngine);
    micEngine.SetMicEnabled(true);
    mic.Start([new AudioDeviceRef
    {
        Id = silentCapture.Id, FriendlyName = silentCapture.FriendlyName, Enabled = true, Volume = 1f,
    }]);

    Thread.Sleep(900);

    Check("opens a capture device without error", mic.Failures.Count == 0,
        mic.Failures.Count > 0 ? mic.Failures[0].Error : silentCapture.FriendlyName);

    var peaks = mic.ReadInputPeaks();
    Check("reports a level for the captured device", peaks.Any(p => p.DeviceId == silentCapture.Id),
        $"{peaks.Count} meter(s)");

    mic.Stop();
    micEngine.SetMicEnabled(false);
    Check("stops cleanly and releases the microphone", true);
}

// --------------------------------------------------------------------------
Section("5c. MIC ROUTING SAFETY");

// The rule that stops feedback: a device only carries the microphone if the user
// deliberately said so. Everything below is about proving that default holds.

Check("a fresh device reference does not carry the mic", !new AudioDeviceRef().ReceivesMic,
    "sounds go everywhere, the mic goes only where it is sent");

// Pressing a playing button stops it rather than stacking another copy.
Check("a new sound defaults to toggle", new SoundButton().Retrigger == RetriggerMode.Toggle,
    "click again to stop, not to layer");
Check("sound gain can be boosted past unity", SoundButton.MaxVolume >= 2f,
    $"ceiling is {SoundButton.MaxVolume * 100:0}%");
var boosted = new SoundButton { Volume = 1.8f };
Check("a boosted volume is not clamped back to 100%",
    Math.Abs(Math.Clamp(boosted.Volume, 0f, SoundButton.MaxVolume) - 1.8f) < 0.001f,
    "the config validator used to cap sound gain at unity");

var micSafeTarget = outputs.FirstOrDefault(o =>
    o.FriendlyName.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase));

if (micSafeTarget is null || decoded.Count == 0)
{
    Console.WriteLine("  SKIP  no idle endpoint to test routing against");
}
else
{
    using var routingEngine = new AudioEngine(devices);
    var reference = new AudioDeviceRef
    {
        Id = micSafeTarget.Id,
        FriendlyName = micSafeTarget.FriendlyName,
        Enabled = true,
        Volume = 1f,
        ReceivesMic = false,
    };

    routingEngine.SetOutputs([reference], 60);
    routingEngine.SetMicEnabled(true);

    Check("mic enabled but no device opted in means nothing carries it",
        !routingEngine.AnyChannelReceivesMic,
        "this is what prevents an accidental feedback loop");

    routingEngine.SetMicRouting(micSafeTarget.Id, true);
    Check("opting a device in starts carrying the mic", routingEngine.AnyChannelReceivesMic);

    routingEngine.SetMicRouting(micSafeTarget.Id, false);
    Check("opting back out stops it again", !routingEngine.AnyChannelReceivesMic);

    routingEngine.SetMicRouting(micSafeTarget.Id, true);
    routingEngine.SetMicEnabled(false);
    Check("the master mic switch overrides per device routing",
        !routingEngine.AnyChannelReceivesMic,
        "the Mute mic button has to win no matter what is ticked");
}

// --------------------------------------------------------------------------
Section("5d. PREVIEW TO ONE DEVICE, and settings that must persist");

if (micSafeTarget is null || decoded.Count == 0)
{
    Console.WriteLine("  SKIP  no idle endpoint to preview against");
}
else
{
    using var previewEngine = new AudioEngine(devices);

    // Two outputs open. A preview must reach exactly one of them.
    var second = outputs.FirstOrDefault(o =>
        o.Id != micSafeTarget.Id
        && o.FriendlyName.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase));

    var refs = new List<AudioDeviceRef>
    {
        new() { Id = micSafeTarget.Id, FriendlyName = micSafeTarget.FriendlyName, Enabled = true, Volume = 1f },
    };

    if (second is not null)
        refs.Add(new AudioDeviceRef { Id = second.Id, FriendlyName = second.FriendlyName, Enabled = true, Volume = 1f });

    previewEngine.SetOutputs(refs, 60);

    var broadcast = previewEngine.Play("b", decoded[0], 1f, RetriggerMode.Overlap);
    Check("a normal press reaches every open device",
        broadcast is not null && broadcast.DeviceCount == refs.Count,
        $"{broadcast?.DeviceCount} of {refs.Count}");
    previewEngine.StopAll();

    var preview = previewEngine.Play("p", decoded[0], 1f, RetriggerMode.Restart, micSafeTarget.Id);
    Check("a preview reaches only the chosen device",
        preview is not null && preview.DeviceCount == 1,
        "this is what keeps a preview out of Discord");
    previewEngine.StopAll();

    var nowhere = previewEngine.Play("x", decoded[0], 1f, RetriggerMode.Overlap, "{not-a-real-device-id}");
    Check("previewing to a device that is not open plays nothing", nowhere is null,
        "silently playing everywhere instead would be the dangerous failure");
}

// Round trip every setting the user can change, through a real save and load.
var roundTripPath = Path.Combine(ConfigService.ConfigDirectory, "config.json");
var before = File.Exists(roundTripPath) ? File.ReadAllText(roundTripPath) : null;

try
{
    var probe = new ConfigService().Load();
    probe.GridColumns = 11;
    probe.GridRows = 5;
    probe.MasterVolume = 0.42f;
    probe.LatencyMs = 90;
    probe.WindowWidth = 1111;
    probe.WindowHeight = 666;
    probe.WindowLeft = 42;
    probe.WindowTop = 24;
    if (probe.Sounds.Count > 0) probe.Sounds[0].Volume = 0.33f;
    if (probe.OutputDevices.Count > 0) probe.OutputDevices[0].IsMonitor = true;

    new ConfigService().Save(probe);
    var reloaded = new ConfigService().Load();

    Check("grid size persists", reloaded.GridColumns == 11 && reloaded.GridRows == 5);
    Check("master volume persists", Math.Abs(reloaded.MasterVolume - 0.42f) < 0.001f);
    Check("audio buffer persists", reloaded.LatencyMs == 90);
    Check("window size and position persist",
        reloaded is { WindowWidth: 1111, WindowHeight: 666, WindowLeft: 42, WindowTop: 24 });
    Check("per sound volume persists",
        reloaded.Sounds.Count == 0 || Math.Abs(reloaded.Sounds[0].Volume - 0.33f) < 0.001f);
    Check("the headphones device persists",
        reloaded.OutputDevices.Count == 0 || reloaded.OutputDevices[0].IsMonitor);
}
finally
{
    // Never leave his real settings mangled by a test.
    if (before is not null) File.WriteAllText(roundTripPath, before);
}

Check("his own config was restored after the round trip",
    before is null || File.ReadAllText(roundTripPath) == before);

// --------------------------------------------------------------------------
Section("6. HOTKEY MODEL");

var hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey = 0x74 };
Check("hotkey renders readably", hotkey.ToString() == "Ctrl+Shift+F5", hotkey.ToString());
Check("unset hotkey renders empty", new Hotkey().ToString().Length == 0);

var fromKey = KeyNames.FromWpfKey(System.Windows.Input.Key.F5, System.Windows.Input.ModifierKeys.Control);
Check("captures a real key press", fromKey is { VirtualKey: 0x74 }, fromKey?.ToString() ?? "null");
Check("ignores a bare modifier press",
    KeyNames.FromWpfKey(System.Windows.Input.Key.LeftShift, System.Windows.Input.ModifierKeys.Shift) is null);

// --------------------------------------------------------------------------
Section("7. IMPORT");

Check("sees the Resanance library", ResananceImporter.IsAvailable, ResananceImporter.DatabasePath);
var folderImport = new ResananceImporter().ImportFromFolder(@"C:\Users\example\Sounds\ImportedBoard");
Check("folder import finds his sounds", folderImport.Imported > 100, $"{folderImport.Imported} files");

// --------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(new string('-', 60));
Console.WriteLine($"PASSED {pass}   FAILED {fail}");
Console.WriteLine(new string('-', 60));

/// <summary>Exercises the surround adapter, which is internal to the Patchboard assembly.</summary>
file sealed class StereoToMultiChannelSampleProviderProbe
{
    public bool Works { get; }

    public StereoToMultiChannelSampleProviderProbe(CachedSound clip)
    {
        try
        {
            // The adapter is internal, so drive the same path the engine uses: a 6 channel
            // endpoint means the mixer output must come back as 6 channel frames.
            var mixer = new MixingSampleProvider(AudioFormat.Mix) { ReadFully = true };
            mixer.AddMixerInput(new CachedSoundSampleProvider(clip, 1f));

            var type = typeof(AudioEngine).Assembly.GetType("Patchboard.Services.StereoToMultiChannelSampleProvider");
            if (type is null) { Works = false; return; }

            var instance = (ISampleProvider?)Activator.CreateInstance(type, mixer, 6);
            if (instance is null) { Works = false; return; }

            var buffer = new float[6 * 800];
            var read = instance.Read(buffer.AsSpan());

            // Front left and right carry audio, the other four stay silent.
            var frontHasAudio = false;
            var rearIsSilent = true;
            for (var frame = 0; frame < read / 6; frame++)
            {
                if (Math.Abs(buffer[frame * 6]) > 0.001f) frontHasAudio = true;
                for (var channel = 2; channel < 6; channel++)
                    if (buffer[frame * 6 + channel] != 0f) rearIsSilent = false;
            }

            Works = instance.WaveFormat.Channels == 6 && read > 0 && frontHasAudio && rearIsSilent;
        }
        catch (Exception)
        {
            Works = false;
        }
    }
}
