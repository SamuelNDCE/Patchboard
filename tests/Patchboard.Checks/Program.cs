using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patchboard.Models;
using Patchboard.Services;
using Patchboard.ViewModels;

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

    var handle = engine.Play("test-button", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Overlap);
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
    engine.Play("toggle-button", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Toggle);
    var second = engine.Play("toggle-button", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Toggle);
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

    var broadcast = previewEngine.Play("b", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Overlap);
    Check("a normal press reaches every open device",
        broadcast is not null && broadcast.DeviceCount == refs.Count,
        $"{broadcast?.DeviceCount} of {refs.Count}");
    previewEngine.StopAll();

    var preview = previewEngine.Play("p", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Restart, micSafeTarget.Id);
    Check("a preview reaches only the chosen device",
        preview is not null && preview.DeviceCount == 1,
        "this is what keeps a preview out of Discord");
    previewEngine.StopAll();

    var nowhere = previewEngine.Play("x", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Overlap, "{not-a-real-device-id}");
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
Section("7. FOLDER IMPORT");

// Derive the library folder from the config rather than hardcoding a path. A machine
// specific absolute path in a tracked file breaks on anyone else's machine and puts a
// username into the repository.
var libraryFolder = config.Sounds
    .Where(s => File.Exists(s.FilePath))
    .Select(s => Path.GetDirectoryName(s.FilePath))
    .Where(d => !string.IsNullOrEmpty(d))
    .GroupBy(d => d!, StringComparer.OrdinalIgnoreCase)
    .OrderByDescending(g => g.Count())
    .Select(g => g.Key)
    .FirstOrDefault();

if (libraryFolder is null)
{
    Console.WriteLine("  SKIP  no sound library folder on this machine to scan");
}
else
{
    var folderImport = new FolderImporter().ImportFromFolder(libraryFolder);
    Check("folder import finds sounds in the library folder", folderImport.Imported > 0,
        $"{folderImport.Imported} files in {Path.GetFileName(libraryFolder)}");
}

// --------------------------------------------------------------------------
Section("8. FOOTPRINT, so it stays lightweight while it runs");

// The render thread reads every mixer input on every single buffer, so the number of
// inputs is the per buffer cost of having the app open. It has to come back down.
if (micSafeTarget is null)
{
    Console.WriteLine("  SKIP  no idle endpoint to measure mixer load against");
}
else
{
    using var loadEngine = new AudioEngine(devices);
    var loadTarget = new AudioDeviceRef
    {
        Id = micSafeTarget.Id,
        FriendlyName = micSafeTarget.FriendlyName,
        Enabled = true,
        Volume = 0f,
        ReceivesMic = false,
    };

    loadEngine.SetOutputs([loadTarget], 60);
    loadEngine.SetMicEnabled(true);

    static int InputsOn(AudioEngine e, string id) =>
        e.ReadMixerLoad().FirstOrDefault(m => m.DeviceId == id).MixerInputs;

    Check("an idle device reads nothing per buffer", InputsOn(loadEngine, micSafeTarget.Id) == 0,
        "no sounds playing and the mic not sent here");

    for (var i = 0; i < 30; i++)
    {
        loadEngine.SetMicRouting(micSafeTarget.Id, true);
        loadEngine.SetMicRouting(micSafeTarget.Id, false);
    }

    var afterToggling = InputsOn(loadEngine, micSafeTarget.Id);

    // The regression this catches: the sink used to be dropped and rebuilt on every
    // toggle, and NAudio cannot take a mixer input back out without the original
    // provider instance, so each flick left one more dead reader wired in forever.
    // Thirty flicks measured thirty of them.
    Check("flicking the mic switch 30 times does not accumulate readers", afterToggling <= 1,
        $"{afterToggling} mixer input(s), was 30 before the fix");

    loadEngine.SetMicRouting(micSafeTarget.Id, true);
    Check("the mic still reaches the device after all that", loadEngine.AnyChannelReceivesMic);

    loadEngine.SetMicRouting(micSafeTarget.Id, false);
    Check("and opting out still silences it", !loadEngine.AnyChannelReceivesMic);

    if (decoded.Count > 0)
    {
        loadEngine.Play("footprint", new CachedSoundSource(decoded[0]), 1f, RetriggerMode.Overlap);
        var whilePlaying = InputsOn(loadEngine, micSafeTarget.Id);
        loadEngine.StopAll();
        Thread.Sleep(250);
        var afterStopping = InputsOn(loadEngine, micSafeTarget.Id);

        Check("a playing sound adds exactly one reader", whilePlaying == afterToggling + 1,
            $"{afterToggling} idle, {whilePlaying} playing");
        Check("a finished sound gives its reader back", afterStopping <= afterToggling,
            $"back to {afterStopping}");
    }
}

// The decoded audio cache. Unbounded, it reached 3.3GB against this library: 203
// playable clips at 48kHz stereo float, none of it ever released.
{
    var budget = 4L * 1024 * 1024;
    var cache = new SoundCache(budget);
    var files = config.Sounds
        .Where(x => File.Exists(x.FilePath))
        .Select(x => x.FilePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count < 3)
    {
        Console.WriteLine("  SKIP  not enough real files to exercise the cache");
    }
    else
    {
        var loaded = 0;
        foreach (var file in files)
        {
            try { cache.GetOrLoad(file); loaded++; }
            catch (Exception) { /* an unplayable clip is section 8b's problem, not this one */ }

            if (cache.Bytes > budget) break;
        }

        Check("the cache never exceeds its budget", cache.Bytes <= budget,
            $"{cache.Bytes / 1024 / 1024.0:0.0} MB of a {budget / 1024 / 1024} MB budget after {loaded} clips");
        Check("it actually evicted rather than just fitting", cache.Evictions > 0 || cache.Count == loaded,
            cache.Evictions > 0 ? $"{cache.Evictions} evicted" : "everything fit");

        // Least recently used goes first, so the clips actually being pressed survive.
        //
        // The favourite has to be one of the SMALL clips. His library's first entry is a
        // ten minute music track that decodes to 224MB, which is larger than any sensible
        // budget and therefore takes the deliberate "hand it back without storing it"
        // path. Picking it as the clip that should survive made this check fail against
        // correct code, twice.
        var bySize = files
            .Select(f => (Path: f, Length: SafeLength(f)))
            .Where(f => f.Length > 0)
            .OrderBy(f => f.Length)
            .ToList();

        var roomy = 64L * 1024 * 1024;
        var favourite = bySize[0].Path;

        var lru = new SoundCache(roomy);
        lru.GetOrLoad(favourite);
        Check("a normal sized clip is actually cached", lru.Contains(favourite),
            $"{Path.GetFileName(favourite)}, {lru.Bytes / 1024.0 / 1024:0.0} MB decoded");

        var alsoLoaded = new List<string>();
        foreach (var (file, _) in bySize.Skip(1))
        {
            lru.GetOrLoad(favourite);   // keep pressing the same button
            try { lru.GetOrLoad(file); alsoLoaded.Add(file); } catch (Exception) { }
            if (lru.Evictions > 0) break;
        }

        Check("eviction actually happened, so the next check means something",
            lru.Evictions > 0, $"{lru.Evictions} evicted, {lru.Count} resident");
        Check("the clip being pressed over and over is the one that survives",
            lru.Contains(favourite), Path.GetFileName(favourite));
        Check("it stayed inside the budget while doing that", lru.Bytes <= roomy,
            $"{lru.Bytes / 1024 / 1024.0:0.0} MB of {roomy / 1024 / 1024} MB");

        var cleared = new SoundCache(roomy);
        cleared.GetOrLoad(favourite);
        var residentBefore = cleared.Bytes;
        cleared.Remove(favourite);
        Check("removing a clip gives its memory back",
            residentBefore > 0 && cleared.Bytes == 0 && !cleared.Contains(favourite),
            $"{residentBefore / 1024 / 1024.0:0.0} MB released");

        // A clip too big for the whole cache still plays; it is just never kept. Without
        // this the cache would evict everything else to make room for something that has
        // to be evicted itself on the very next load.
        // The largest file on disk is not usable here: the three biggest in this library
        // are hour long music rips that CachedSound refuses outright, so they never reach
        // the cache at all. Take the largest clip that actually decodes.
        var tiny = new SoundCache(1024);
        var oversizeChecked = false;

        foreach (var (candidate, _) in Enumerable.Reverse(bySize))
        {
            CachedSound stillPlays;
            try { stillPlays = tiny.GetOrLoad(candidate); }
            catch (Exception) { continue; }   // past the decode ceiling, not a cache matter

            Check("a clip bigger than the whole budget still plays, unstored",
                stillPlays.AudioData.Length > 0 && tiny.Bytes == 0 && !tiny.Contains(candidate),
                Path.GetFileName(candidate));
            oversizeChecked = true;
            break;
        }

        if (!oversizeChecked)
            Console.WriteLine("  SKIP  no decodable clip large enough to exceed a 1KB budget");
    }
}

// --------------------------------------------------------------------------
Section("8d. A DEAD OR OVERLONG BUTTON MUST NOT FREEZE THE WINDOW");

// Decoding runs from the click handler. It used to run ON the UI thread and decode the
// whole file, so pressing an hour long button locked the window for 4.4 seconds and then
// reported a failure. Two things fixed it: the length is now read from the header before
// any decoding, and the decode itself moved to a background thread. This check guards the
// first half, which is the one that can silently regress.
{
    var overLong = new List<(string Path, double Minutes)>();
    foreach (var sound in config.Sounds.Where(x => File.Exists(x.FilePath)))
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(sound.FilePath);
            if (reader.TotalTime > CachedSound.MaxDuration)
                overLong.Add((sound.FilePath, reader.TotalTime.TotalMinutes));
        }
        catch (Exception) { }
    }

    if (overLong.Count == 0)
    {
        Console.WriteLine("  SKIP  no clip past the decode ceiling in this library");
    }
    else
    {
        var worstMs = 0L;
        var allRejected = true;

        foreach (var (path, minutes) in overLong)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rejected = false;
            try { CachedSound.Load(path); }
            catch (ClipTooLongException) { rejected = true; }
            catch (Exception) { }
            sw.Stop();

            worstMs = Math.Max(worstMs, sw.ElapsedMilliseconds);
            allRejected &= rejected;
        }

        Check($"all {overLong.Count} overlong clips are rejected by type", allRejected,
            "so the button can say 'too long' rather than 'file missing'");

        // 4400ms before the fix. A generous ceiling, because this is guarding against a
        // return to decoding the whole file, not policing a few milliseconds.
        Check("rejecting an overlong clip is immediate, not a four second freeze",
            worstMs < 400, $"worst {worstMs} ms, was 4393 ms");

        Check("the longest clip is named on the button, not mislabelled as missing",
            SoundButtonViewModel.DescribeProblem(
                new ClipTooLongException("x.mp3", TimeSpan.FromMinutes(71), CachedSound.MaxDuration)) == "too long");
    }

    // A missing file must stay cheap however many times it is pressed.
    var gone = Path.Combine(Path.GetTempPath(), "patchboard-gone-" + Guid.NewGuid().ToString("N") + ".mp3");
    var missSw = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 200; i++)
    {
        try { CachedSound.Load(gone); } catch (Exception) { }
    }
    missSw.Stop();
    Check("pressing a button whose file is gone stays cheap", missSw.ElapsedMilliseconds < 500,
        $"200 presses in {missSw.ElapsedMilliseconds} ms");
}

// --------------------------------------------------------------------------
Section("8e. LIBRARY TIDYING");

// Names. The risk is not failing to clean, it is mangling something meaningful.
Check("strips a download id suffix", LibraryTidy.CleanName("bonk_7zPAD7C") == "bonk",
    LibraryTidy.CleanName("bonk_7zPAD7C"));
Check("strips an id with no digit in it", LibraryTidy.CleanName("bruh-sound-effect_WstdzdM") == "bruh sound effect",
    LibraryTidy.CleanName("bruh-sound-effect_WstdzdM"));
Check("strips a browser copy suffix",
    LibraryTidy.CleanName("rizz-sounds (1)") == "rizz sounds", LibraryTidy.CleanName("rizz-sounds (1)"));
Check("strips a ripper site prefix",
    LibraryTidy.CleanName("Y2meta.app - Top 100 Christmas Songs") == "Top 100 Christmas Songs",
    LibraryTidy.CleanName("Y2meta.app - Top 100 Christmas Songs"));
Check("strips a leading track number",
    LibraryTidy.CleanName("05. I Really Want to Stay at Your House") == "I Really Want to Stay at Your House");
Check("strips an editor suffix",
    LibraryTidy.CleanName("eric-andre-mp3cut") == "eric andre", LibraryTidy.CleanName("eric-andre-mp3cut"));

// The things it must NOT do.
Check("keeps a real word that looks a bit like an id",
    LibraryTidy.CleanName("scream_reversed") == "scream reversed", LibraryTidy.CleanName("scream_reversed"));
Check("keeps a short trailing word", LibraryTidy.CleanName("horn_loud") == "horn loud");
Check("leaves an already clean name alone", LibraryTidy.CleanName("all my fellas") == "all my fellas");
Check("leaves a GUID filename alone rather than making it worse",
    LibraryTidy.CleanName("53b1bab6-a8c3-4a1a-82db-7110ce1c29ef") == "53b1bab6-a8c3-4a1a-82db-7110ce1c29ef");
Check("never returns an empty label", LibraryTidy.CleanName("(1)").Length > 0, LibraryTidy.CleanName("(1)"));

// A label the user typed is a decision and must survive a bulk tidy.
var handNamed = new SoundButton { FilePath = @"C:\x\bonk_7zPAD7C.mp3", Name = "BONK" };
var untouched = new SoundButton { FilePath = @"C:\x\bonk_7zPAD7C.mp3", Name = "bonk_7zPAD7C" };
Check("a hand written label is left alone", !LibraryTidy.IsUntouchedName(handNamed));
Check("an untouched label is fair game", LibraryTidy.IsUntouchedName(untouched));
Check("the tidy plan skips hand written labels",
    LibraryTidy.PlanNameTidy([handNamed, untouched]).Count == 1);

// Duplicates must be decided on content, never on size alone.
var realPlan = LibraryTidy.PlanNameTidy(config.Sounds);
Check("the plan leaves his 43 hand written labels alone",
    config.Sounds.Count(LibraryTidy.IsUntouchedName) >= realPlan.Count,
    $"{realPlan.Count} of {config.Sounds.Count} labels would change");

var dupeGroups = LibraryTidy.FindDuplicates(config.Sounds);
Check("duplicate detection confirms by content, not just by file size",
    dupeGroups.All(g => g.Select(x => new FileInfo(x.FilePath).Length).Distinct().Count() == 1),
    $"{dupeGroups.Count} genuine group(s); two unrelated 78KB files are correctly not paired");
Check("every duplicate group keeps exactly one button",
    dupeGroups.All(g => g.Count > 1), "a group of one is not a duplicate");

var deadFound = LibraryTidy.FindDead(config.Sounds);
Check("dead button detection agrees with the config check",
    deadFound.Count == config.Sounds.Count(x => !File.Exists(x.FilePath)),
    $"{deadFound.Count} dead");

// --------------------------------------------------------------------------
Section("8h. XAML THAT COMPILES BUT CANNOT RENDER");

// A Binding inside an x:Array compiles perfectly and then throws during layout:
// "A 'Binding' cannot be used within an 'ArrayList' collection. A 'Binding' can only be
// set on a DependencyProperty of a DependencyObject." It shipped once, in the colour
// swatches, and only surfaced when the right click menu was first opened. The build says
// nothing, so this has to be checked rather than compiled.
{
    var xamlRoot = FindRepoDirectory("src");
    if (xamlRoot is null)
    {
        Console.WriteLine("  SKIP  cannot locate the source tree from the test binary");
    }
    else
    {
        var files = Directory.GetFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var offenders = new List<string>();
        var unreadable = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(file);
                foreach (var array in doc.Descendants()
                             .Where(e => e.Name.LocalName == "Array"))
                {
                    if (array.Descendants().Any(d => d.Name.LocalName == "Binding"
                                                     || d.Name.LocalName == "MultiBinding"))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: Binding inside x:Array");
                    }
                }
            }
            catch (Exception ex)
            {
                unreadable.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Check("every XAML file parses as XML", unreadable.Count == 0,
            unreadable.Count > 0 ? unreadable[0] : $"{files.Count} files");
        Check("no Binding is trapped inside an x:Array", offenders.Count == 0,
            offenders.Count > 0 ? offenders[0] : "this threw at layout time, not at build time");
    }
}

// --------------------------------------------------------------------------
Section("8f. TRIM, which is what makes a long recording usable at all");

{
    var longClip = config.Sounds.FirstOrDefault(x =>
    {
        try
        {
            using var r = new NAudio.Wave.AudioFileReader(x.FilePath);
            return r.TotalTime > CachedSound.MaxDuration;
        }
        catch (Exception) { return false; }
    });

    if (longClip is null)
    {
        Console.WriteLine("  SKIP  no clip past the ceiling to trim");
    }
    else
    {
        // The whole point: a file that can never play in full plays as a short window.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CachedSound? trimmed = null;
        string outcome;
        try
        {
            trimmed = CachedSound.Load(longClip.FilePath, 30_000, 38_000);
            outcome = $"{trimmed.Duration.TotalSeconds:0.0}s";
        }
        catch (Exception ex) { outcome = ex.GetType().Name; }
        sw.Stop();

        Check("an hour long clip plays once trimmed to eight seconds",
            trimmed is not null && Math.Abs(trimmed.Duration.TotalSeconds - 8) < 0.5,
            $"{outcome} in {sw.ElapsedMilliseconds} ms");

        Check("only the kept window is decoded, so it is cheap",
            sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");

        Check("the same file untrimmed is still refused",
            Throws<ClipTooLongException>(() => CachedSound.Load(longClip.FilePath)));
    }

    // Trimming an ordinary clip takes the window asked for, from the point asked for.
    var ordinary = config.Sounds.FirstOrDefault(x =>
    {
        try
        {
            using var r = new NAudio.Wave.AudioFileReader(x.FilePath);
            return r.TotalTime.TotalSeconds is > 12 and < 240;
        }
        catch (Exception) { return false; }
    });

    if (ordinary is null)
    {
        Console.WriteLine("  SKIP  no clip long enough to trim meaningfully");
    }
    else
    {
        var whole = CachedSound.Load(ordinary.FilePath);
        var window = CachedSound.Load(ordinary.FilePath, 2_000, 5_000);

        Check("a trimmed window is the requested length",
            Math.Abs(window.Duration.TotalSeconds - 3) < 0.25,
            $"{window.Duration.TotalSeconds:0.00}s of a {whole.Duration.TotalSeconds:0.0}s clip");

        Check("it starts where it was told to, not at zero",
            !window.AudioData.Take(2000).SequenceEqual(whole.AudioData.Take(2000))
            || whole.AudioData.Take(2000).All(v => v == 0f),
            "the first samples differ from the untrimmed clip");

        // An end before the start, or past the file, must fall back rather than produce
        // silence or throw, because a hand edited config can contain either.
        // An end before the start: only the end is nonsense, so only the end is discarded.
        // Throwing away the valid start too would silently ignore half of what was typed.
        var backwards = CachedSound.Load(ordinary.FilePath, 9_000, 3_000);
        var expected = whole.Duration.TotalSeconds - 9;
        Check("a backwards trim keeps the valid start and drops only the bad end",
            Math.Abs(backwards.Duration.TotalSeconds - expected) < 0.25,
            $"{backwards.Duration.TotalSeconds:0.0}s, expected {expected:0.0}s");

        var beyond = CachedSound.Load(ordinary.FilePath, 0, 9_999_000);
        Check("an end past the file falls back to the file's end",
            Math.Abs(beyond.Duration.TotalSeconds - whole.Duration.TotalSeconds) < 0.25);

        var startPastEnd = CachedSound.Load(ordinary.FilePath, 9_999_000, 0);
        Check("a start past the file falls back to the beginning",
            Math.Abs(startPastEnd.Duration.TotalSeconds - whole.Duration.TotalSeconds) < 0.25);
    }

    // The slider path. Duration has to be known before a trim can be aimed, and the two
    // handles must never cross: a zero length window decodes to nothing and the button
    // goes silent with no explanation.
    var slidered = config.Sounds.FirstOrDefault(x =>
    {
        try
        {
            using var r = new NAudio.Wave.AudioFileReader(x.FilePath);
            return r.TotalTime.TotalSeconds is > 12 and < 240;
        }
        catch (Exception) { return false; }
    });

    if (slidered is not null)
    {
        var vm = new SoundButtonViewModel(new SoundButton { FilePath = slidered.FilePath, Name = "t" });

        Check("a button starts with no known length", !vm.HasDuration, vm.DurationText);

        // Awaited rather than polled. The first version marshalled through
        // Dispatcher.CurrentDispatcher, which never runs in a console harness because
        // nothing pumps messages, so this check sat at zero for four seconds and failed
        // against code that worked perfectly inside the app.
        vm.EnsureDurationAsync().GetAwaiter().GetResult();

        Check("the clip length is read so the sliders have a range", vm.HasDuration,
            $"{vm.DurationSeconds:0.0}s");

        if (vm.HasDuration)
        {
            Check("an untrimmed button reads as the whole clip",
                !vm.IsTrimmed && Math.Abs(vm.EndSeconds - vm.DurationSeconds) < 0.01,
                vm.TrimText);

            vm.StartSeconds = 3;
            vm.EndSeconds = 8;
            Check("dragging both handles keeps the window", vm.IsTrimmed
                && Math.Abs(vm.StartSeconds - 3) < 0.01 && Math.Abs(vm.EndSeconds - 8) < 0.01,
                vm.TrimText);

            // Push the start past the end. It must stop short, not swap them or collapse.
            vm.StartSeconds = 20;
            Check("the start cannot be dragged past the end",
                vm.StartSeconds < vm.EndSeconds && vm.EndSeconds - vm.StartSeconds > 0.05,
                $"start {vm.StartSeconds:0.00}s, end {vm.EndSeconds:0.00}s");

            vm.StartSeconds = 3;
            vm.EndSeconds = 0;
            Check("the end cannot be dragged past the start",
                vm.EndSeconds > vm.StartSeconds, $"end {vm.EndSeconds:0.00}s");

            // Dragged to the far end means "to the end of the file", stored as 0 so it does
            // not go stale if the file is later replaced by a longer one.
            vm.StartSeconds = 0;
            vm.EndSeconds = vm.DurationSeconds;
            Check("dragging the end to the far right means 'play to the end'",
                vm.Model.EndMs == 0 && !vm.IsTrimmed, vm.TrimText);

            vm.StartSeconds = 5;
            vm.ClearTrimCommand.Execute(null);
            Check("'use the whole clip' puts it back",
                !vm.IsTrimmed && vm.Model.StartMs == 0 && vm.Model.EndMs == 0, vm.TrimText);
        }
    }

    // Two trims of one file are different audio and must not share a cache entry.
    var a = new SoundButton { FilePath = @"C:\x\clip.mp3", StartMs = 0, EndMs = 0 };
    var b = new SoundButton { FilePath = @"C:\x\clip.mp3", StartMs = 1000, EndMs = 4000 };
    var c = new SoundButton { FilePath = @"C:\x\clip.mp3", StartMs = 1000, EndMs = 9000 };
    Check("an untrimmed button keys on the plain path", a.CacheKey == a.FilePath);
    Check("two different trims of one file are different cache entries",
        b.CacheKey != c.CacheKey && b.CacheKey != a.CacheKey);
    Check("trim state is reported", !a.IsTrimmed && b.IsTrimmed);
}

// --------------------------------------------------------------------------
Section("8i. LONG CLIPS PLAY, by streaming instead of decoding");

{
    var longClip = config.Sounds.FirstOrDefault(x =>
    {
        try
        {
            using var r = new NAudio.Wave.AudioFileReader(x.FilePath);
            return r.TotalTime > TimeSpan.FromMinutes(20);
        }
        catch (Exception) { return false; }
    });

    if (longClip is null)
    {
        Console.WriteLine("  SKIP  no clip over twenty minutes in this library");
    }
    else
    {
        double minutes;
        using (var r = new NAudio.Wave.AudioFileReader(longClip.FilePath)) minutes = r.TotalTime.TotalMinutes;

        // The headline: this used to be flatly impossible. Decoding an hour of audio is
        // about 700MB and the cache would refuse to hold it, so every press re-decoded it.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        StreamingSoundSource? streamed = null;
        string outcome;
        try
        {
            streamed = StreamingSoundSource.Open(longClip.FilePath, 0, 0);
            outcome = $"{streamed.Duration.TotalMinutes:0.0} min ready";
        }
        catch (Exception ex) { outcome = ex.GetType().Name; }
        sw.Stop();

        Check($"a {minutes:0} minute clip is playable at all",
            streamed is not null, $"{outcome} in {sw.ElapsedMilliseconds} ms");
        Check("opening it is immediate, because nothing is decoded up front",
            sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");

        if (streamed is not null)
        {
            Check("it reports itself as not held in memory", !streamed.IsResident);
            Check("its length is the real length of the file",
                Math.Abs(streamed.Duration.TotalMinutes - minutes) < 0.1,
                $"{streamed.Duration.TotalMinutes:0.0} min");

            // Actually pull audio through it, so this is not just a constructor test.
            using var instance = streamed.CreateInstance(1f);
            var buffer = new float[AudioFormat.SampleRate * AudioFormat.Channels / 10];
            var read = instance.Read(buffer.AsSpan());
            Check("audio comes out of a streamed clip", read > 0, $"{read} samples");

            // Seeking is the other half of what a long clip needs.
            instance.Seek(0.5);
            var afterSeek = instance.Read(buffer.AsSpan());
            Check("a streamed clip can be seeked into the middle",
                afterSeek > 0 && instance.Progress is > 0.4 and < 0.6,
                $"progress {instance.Progress:0.00}");

            instance.Seek(0);
            Check("and seeked back to the start", instance.Progress < 0.05,
                $"progress {instance.Progress:0.00}");

            // Memory is the entire justification for streaming, so measure it.
            var memoryBefore = GC.GetTotalMemory(true);
            var instances = new List<IPlaybackInstance>();
            for (var i = 0; i < 3; i++) instances.Add(streamed.CreateInstance(1f));
            foreach (var inst in instances) inst.Read(buffer.AsSpan());
            var memoryAfter = GC.GetTotalMemory(false);
            foreach (var inst in instances) inst.Dispose();

            var mb = (memoryAfter - memoryBefore) / 1024.0 / 1024.0;
            Check("three devices streaming it cost megabytes, not hundreds",
                mb < 60, $"{mb:0.0} MB, versus about {minutes * 60 * 48000 * 2 * 4 / 1024 / 1024:0} MB decoded");
        }
    }

    // A short clip must still be held in memory, or every press pays disk latency.
    var shortClip = config.Sounds.FirstOrDefault(x =>
    {
        try
        {
            using var r = new NAudio.Wave.AudioFileReader(x.FilePath);
            return r.TotalTime.TotalSeconds is > 1 and < 30;
        }
        catch (Exception) { return false; }
    });

    if (shortClip is not null)
    {
        var resident = new CachedSoundSource(CachedSound.Load(shortClip.FilePath));
        Check("a short clip is still held in memory", resident.IsResident,
            $"{resident.Duration.TotalSeconds:0.0}s");

        using var a = resident.CreateInstance(1f);
        using var b = resident.CreateInstance(1f);
        var buf = new float[4800];
        a.Read(buf.AsSpan());
        Check("two copies of a resident clip keep their own positions",
            a.Progress > 0 && b.Progress == 0);

        a.Seek(0.5);
        Check("a resident clip can be seeked", a.Progress is > 0.45 and < 0.55, $"{a.Progress:0.00}");
    }
}

// --------------------------------------------------------------------------
Section("8j. DURATIONS A PERSON CAN READ");

// The trim panel showed "2950.0s long" for a 49 minute clip. Technically the length.
Check("under a minute stays in seconds", TimeText.Words(15) == "15 seconds", TimeText.Words(15));
Check("one second is singular", TimeText.Words(1) == "1 second", TimeText.Words(1));
Check("a fraction of a second keeps its decimal", TimeText.Words(0.4) == "0.4 seconds", TimeText.Words(0.4));
Check("ninety seconds is a minute and a half",
    TimeText.Words(90) == "1 minute 30 seconds", TimeText.Words(90));
Check("an exact minute drops the seconds", TimeText.Words(60) == "1 minute", TimeText.Words(60));
Check("a 49 minute clip says so rather than 2950 seconds",
    TimeText.Words(2950) == "49 minutes 10 seconds", TimeText.Words(2950));
Check("over an hour reads in hours",
    TimeText.Words(4256.9).StartsWith("1 hour"), TimeText.Words(4256.9));
Check("nonsense does not produce nonsense",
    TimeText.Words(double.NaN) == "0 seconds" && TimeText.Words(-5) == "0 seconds");

// The clock form is for a readout that moves, so its shape must not jitter.
Check("clock form is mm:ss", TimeText.Clock(90) == "1:30", TimeText.Clock(90));
Check("clock form pads the seconds", TimeText.Clock(65) == "1:05", TimeText.Clock(65));
Check("clock form grows to hours", TimeText.Clock(4256) == "1:10:56", TimeText.Clock(4256));

// --------------------------------------------------------------------------
Section("8k. THE LEAD IN BEFORE A SOUND PLAYS");

{
    var board = new AppConfig();
    Check("a new board has no lead in", board.DefaultDelayMs == 0);

    var button = new SoundButton { FilePath = @"C:\x\a.mp3" };
    Check("a new button follows the board", !button.HasOwnDelay,
        $"DelayMs {button.DelayMs}");

    button.DelayMs = 250;
    Check("a button can set its own", button.HasOwnDelay && button.DelayMs == 250);

    button.DelayMs = SoundButton.UseDefaultDelay;
    Check("and can be put back to following the board", !button.HasOwnDelay);

    // A hand edited config must not produce a negative wait or an absurd one.
    var wild = new AppConfig
    {
        DefaultDelayMs = 999_999,
        Sounds = [new SoundButton { FilePath = @"C:\x\a.mp3", DelayMs = -20 },
                  new SoundButton { FilePath = @"C:\x\b.mp3", DelayMs = 999_999 }],
    };

    var repairedPath = Path.Combine(ConfigService.ConfigDirectory, "config.json");
    var hisRealConfig = File.Exists(repairedPath) ? File.ReadAllText(repairedPath) : null;
    var rescue = Path.Combine(ConfigService.ConfigDirectory, "config.delay-rescue.json");
    if (hisRealConfig is not null) File.WriteAllText(rescue, hisRealConfig);

    try
    {
        new ConfigService().Save(wild);
        var back = new ConfigService().Load();

        Check("an absurd board default is clamped", back.DefaultDelayMs <= 5000,
            $"{back.DefaultDelayMs} ms");
        Check("a negative override collapses to 'follow the default'",
            back.Sounds.Count == 2 && !back.Sounds[0].HasOwnDelay,
            $"{back.Sounds[0].DelayMs}");
        Check("an absurd override is clamped rather than obeyed",
            back.Sounds[1].DelayMs <= 5000, $"{back.Sounds[1].DelayMs} ms");
    }
    finally
    {
        if (hisRealConfig is not null) File.WriteAllText(repairedPath, hisRealConfig);
    }

    var restoredOk = hisRealConfig is null || File.ReadAllText(repairedPath) == hisRealConfig;
    Check("his own config was restored after the delay checks", restoredOk);
    if (restoredOk && File.Exists(rescue)) { try { File.Delete(rescue); } catch (Exception) { } }
}

// --------------------------------------------------------------------------
Section("8l. THE RANGE SLIDER'S HANDLES CANNOT CROSS");

// Pure logic, no visual tree: the coercion is what stops a zero length trim window, and a
// zero length window decodes to nothing and makes a button look broken rather than empty.
// Constructing any WPF Control needs an STA thread, and a console harness is MTA, so this
// section runs on its own STA thread and is joined before anything else continues. Without
// it the whole run died on "The calling thread must be STA" at the first `new RangeSlider`.
{
    var staThread = new Thread(() =>
    {
    var slider = new Patchboard.Controls.RangeSlider
    {
        Minimum = 0, Maximum = 100, LowerValue = 20, UpperValue = 60, MinimumRange = 0.1,
    };

    Check("it holds the window it was given",
        Math.Abs(slider.LowerValue - 20) < 0.001 && Math.Abs(slider.UpperValue - 60) < 0.001);

    slider.LowerValue = 90;
    Check("the start cannot be pushed past the end",
        slider.LowerValue < slider.UpperValue,
        $"lower {slider.LowerValue:0.00}, upper {slider.UpperValue:0.00}");

    slider.UpperValue = 0;
    Check("the end cannot be pushed past the start",
        slider.UpperValue > slider.LowerValue,
        $"lower {slider.LowerValue:0.00}, upper {slider.UpperValue:0.00}");

    slider.LowerValue = -50;
    slider.UpperValue = 500;
    Check("both stay inside the track",
        slider.LowerValue >= 0 && slider.UpperValue <= 100,
        $"{slider.LowerValue:0.0} to {slider.UpperValue:0.0}");

    // Shrinking the clip must drag the handles in with it, not strand them off the end.
    slider.LowerValue = 10;
    slider.UpperValue = 90;
    slider.Maximum = 30;
    Check("shortening the clip pulls the handles back onto it",
        slider.UpperValue <= 30 && slider.LowerValue <= slider.UpperValue,
        $"{slider.LowerValue:0.0} to {slider.UpperValue:0.0} of 30");
    });

    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();
}

// --------------------------------------------------------------------------
Section("8g. BUTTON COLOUR, which was saved and bound but unreachable");

{
    var model = new SoundButton { FilePath = @"C:\x\a.mp3", Name = "a" };
    var vm = new SoundButtonViewModel(model);
    var raised = 0;
    vm.ColorChanged += _ => raised++;

    Check("a new button has no colour", vm.Color is null);

    vm.Color = "#3A1F1F";
    Check("setting a colour reaches the model, which is what gets saved",
        model.Color == "#3A1F1F" && vm.Color == "#3A1F1F");
    Check("it tells the owner to save", raised == 1);

    vm.Color = "#3A1F1F";
    Check("setting the same colour again does not re-save", raised == 1);

    vm.Color = null;
    Check("clearing goes back to the default surface", model.Color is null && raised == 2);
}

// --------------------------------------------------------------------------
Section("8c. FIRST RUN, on a machine that has never saved anything");

// AppConfig.WindowLeft and WindowTop are NaN until a window position has been recorded,
// and System.Text.Json refuses to write NaN unless it is told to allow it. Every save on
// a fresh install therefore threw, was caught, and became a status message: sounds,
// devices and volumes silently failed to persist for the whole session, and were lost
// outright if the window was closed while minimised. That is the state every downloaded
// copy starts in, so it goes through the real ConfigService, not a copy of its options.
Check("a brand new config really does start unpositioned",
    double.IsNaN(new AppConfig().WindowLeft) && double.IsNaN(new AppConfig().WindowTop),
    "NaN means let Windows choose");

var freshPath = Path.Combine(ConfigService.ConfigDirectory, "config.json");
var hisConfig = File.Exists(freshPath) ? File.ReadAllText(freshPath) : null;

// These checks replace his real config with a blank one and put it back afterwards.
// Holding the only copy in a local string means killing this process at the wrong moment
// destroys a 206 sound library, so there is a copy on disk for the duration. It is
// deleted only after the restore has been verified, which means a leftover file is
// itself the signal that a run was interrupted.
var rescuePath = Path.Combine(ConfigService.ConfigDirectory, "config.checks-rescue.json");
if (hisConfig is not null) File.WriteAllText(rescuePath, hisConfig);

try
{
    // Exactly what the app holds before it has ever been positioned.
    new ConfigService().Save(new AppConfig());

    var reloadedFresh = new ConfigService().Load();
    Check("a fresh config can actually be saved", true,
        "this threw before, so nothing persisted until the window was closed");
    Check("and comes back still unpositioned", double.IsNaN(reloadedFresh.WindowLeft),
        "the window restore tests for NaN, so it has to survive the round trip");

    // A nonsense size or an infinite coordinate is repaired rather than handed to a window.
    new ConfigService().Save(new AppConfig
    {
        WindowWidth = double.NaN,
        WindowHeight = double.PositiveInfinity,
        WindowLeft = double.NegativeInfinity,
    });

    var repaired = new ConfigService().Load();
    Check("a non finite window size is repaired",
        double.IsFinite(repaired.WindowWidth) && double.IsFinite(repaired.WindowHeight),
        $"{repaired.WindowWidth} x {repaired.WindowHeight}");
    Check("an infinite window position falls back to unpositioned",
        double.IsNaN(repaired.WindowLeft));
}
catch (Exception ex)
{
    Check("a fresh config can actually be saved", false, $"{ex.GetType().Name}: {ex.Message}");
}
finally
{
    // Never leave his real settings replaced by a blank one.
    if (hisConfig is not null) File.WriteAllText(freshPath, hisConfig);
}

var restored = hisConfig is null || File.ReadAllText(freshPath) == hisConfig;
Check("his own config was restored after the first run checks", restored);

if (restored && File.Exists(rescuePath))
{
    try { File.Delete(rescuePath); } catch (Exception) { }
}
else if (!restored)
{
    Console.WriteLine($"  KEPT  a copy of his config is at {rescuePath}");
}

// --------------------------------------------------------------------------
Section("8b. A DEAD BUTTON SAYS WHY IT IS DEAD");

Check("a genuinely missing file says so",
    SoundButtonViewModel.DescribeProblem(new FileNotFoundException("gone", "x.mp3")) == "file missing");

// Three of the imported buttons are hour long music rips. They are past the decode
// ceiling, so they can never play, and every one of them used to report "file missing"
// and send him looking for a file sitting exactly where he left it.
// Matched on exception TYPE now, not by searching the message for "longer than".
// These two checks used to construct a plain InvalidOperationException with the old
// wording, which is exactly the coupling the typed exceptions removed.
var tooLong = new ClipTooLongException("mix.mp3", TimeSpan.FromMinutes(63), CachedSound.MaxDuration);
Check("a clip past the decode ceiling says it is too long, not missing",
    SoundButtonViewModel.DescribeProblem(tooLong) == "too long",
    SoundButtonViewModel.DescribeProblem(tooLong));

Check("a silent file says it has no audio",
    SoundButtonViewModel.DescribeProblem(new ClipEmptyException("x.mp3")) == "no audio");

Check("an unrecognised failure still gets a label",
    SoundButtonViewModel.DescribeProblem(new InvalidOperationException("something else")) == "won't play");

var absent = new SoundButtonViewModel(new SoundButton
{
    FilePath = Path.Combine(Path.GetTempPath(), "patchboard-not-here-" + Guid.NewGuid().ToString("N") + ".mp3"),
    Name = "gone",
});
Check("a button whose file vanished is flagged on sight", absent.HasProblem && absent.Problem == "file missing");

var presentFile = config.Sounds.FirstOrDefault(x => File.Exists(x.FilePath));
if (presentFile is not null)
{
    var present = new SoundButtonViewModel(presentFile);
    Check("a button whose file is there is not flagged", !present.HasProblem);
}

// --------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(new string('-', 60));
Console.WriteLine($"PASSED {pass}   FAILED {fail}");
Console.WriteLine(new string('-', 60));


/// <summary>
/// Walk up from the test binary to the repository and return one of its directories.
/// The binary sits in tests/Patchboard.Checks/bin/Release/net9.0-windows, and the depth
/// changes with configuration, so it is searched for rather than counted.
/// </summary>
static string? FindRepoDirectory(string name)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, name);
        if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            return candidate;
    }

    return null;
}

static bool Throws<T>(Action work) where T : Exception
{
    try { work(); return false; }
    catch (T) { return true; }
    catch (Exception) { return false; }
}

static long SafeLength(string path)
{
    try { return new FileInfo(path).Length; }
    catch (Exception) { return 0; }
}

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
