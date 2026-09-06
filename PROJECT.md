# Patchboard

A Windows soundboard that plays one sound to several audio outputs at once, so the
same clip lands in your headphones, your Discord mic and your in-game mic together.
Replaces Resanance.

## What this is

WPF desktop app, .NET 9, NAudio 3.0.1. Single process, no server, no database daemon.
Config is plain JSON on disk. Audio goes straight to WASAPI.

## Hard rules

- **Never change Samuel's system audio configuration.** Do not set default devices, do
  not edit VoiceMeeter config, do not write to `%APPDATA%\Resanance`. The app selects
  devices for its own output only. Set 2026-09-02 at his instruction: "dont mess with
  the audio I will when I can use the app."
- **Never play test audio through his live devices without asking.** Discord and
  VoiceMeeter run permanently on this machine. A test tone reaches other people.
  Verify with device enumeration and unit tests instead.
- **Resanance's data directory is read-only to us.** The importer opens
  `%APPDATA%\Resanance\data\Resanance.db` (LiteDB) for reading only, and copies out.
  Never write back, never delete. He may still want to use Resanance.
- **Do not store absolute machine paths in tracked files.** Sound file paths live in
  the user's own config under `%APPDATA%\Patchboard\config.json`, which is not tracked.
- **Never synthesise mouse or keyboard input, and never call SetForegroundWindow, on
  this machine.** Set 2026-09-02 after a `mouse_event` right click intended for a
  Patchboard tile landed in Samuel's live multiplayer match, because he was playing
  fullscreen at the time and the click went to the game. Verifying a GUI tempts exactly
  this, so the rule is absolute rather than conditional on whether a game looks like it
  is running: you cannot see what is fullscreen from a tool call.

  What is still allowed: launching the app, `GetClientRect` plus `ClientToScreen` plus
  `CopyFromScreen` to capture **only the app's own client rectangle**, and read-only UI
  Automation queries scoped to the Patchboard process. What is not: `InvokePattern.Invoke`,
  `TogglePattern`, `mouse_event`, `SendKeys`, `SetCursorPos`, `SetForegroundWindow`, and
  full-screen capture, which photographs whatever he is actually doing.

  **`CopyFromScreen` at a window's own coordinates is not the same as capturing that
  window.** It reads screen pixels, not window content, so if Patchboard is no longer the
  frontmost thing at those coordinates (he alt-tabbed away, another window covers it) the
  capture silently returns whatever IS on top there instead: proven 2026-09-06, when a
  passive re-capture of an already-open Patchboard window returned his Opera browser
  instead, bookmarks bar and all, because he had switched to YouTube in between. **Before
  any `CopyFromScreen` call, read `GetForegroundWindow` first and compare it to the target
  `hWnd`.** If they don't match, do not capture: say the window is no longer in front and
  wait, rather than silently photographing whatever replaced it. `GetWindowRect` has the
  same failure mode as `GetClientRect` here; the fix is the foreground check, not the rect
  choice, though `GetClientRect` is still preferred over `GetWindowRect` separately because
  the DWM drop-shadow around the window rect bleeds a few pixels of whatever is directly
  behind it even when the window genuinely is in front.

  Anything needing a real click is Samuel's to test. Say so plainly rather than reaching
  for automation.

## Audio architecture, and why

Each sound is decoded ONCE into a float array in memory (`CachedSound`). Every selected
output device gets its own lightweight reader over that same shared buffer. This is the
canonical NAudio pattern and it matters here for three reasons: the outputs stay in
sync because they start from one buffer, the file is read from disk once, and each
output keeps an independent volume and position.

Per output device the chain is:

    CachedSound (shared float[])
      -> CachedSoundSampleProvider   (one per device, own position)
      -> VolumeSampleProvider        (per-sound volume)
      -> MixingSampleProvider        (one per device, mixes concurrent sounds + mic)
      -> WdlResamplingSampleProvider (only if device rate differs)
      -> WasapiOut(device, Shared, eventSync, latency)

`AudioClientShareMode.Shared` is mandatory, not a preference. Exclusive mode seizes the
device and would cut off Discord, the game, and everything else using it.

NAudio 3.0.1 notes, verified against the installed assembly on 2026-09-02, not from docs:
- The package is split. `NAudio.Wasapi` holds the WASAPI players, `WasapiCapture` and
  `MMDeviceEnumerator`. `NAudio.Core` holds the sample providers.
- `MediaFoundationResampler` is NOT present in 3.0.1. Use `WdlResamplingSampleProvider`.
  Any guide telling you otherwise is written for NAudio 2.x.
- `ISampleProvider.Read` takes a `Span<float>` and `IWaveProvider.Read` a `Span<byte>`.
  The 2.x `(buffer, offset, count)` signature is gone, so every custom provider differs
  from every tutorial you will find.
- **`WasapiOut` is `[Obsolete]`.** Build outputs with `WasapiPlayerBuilder`, which returns
  a `WasapiPlayer`. It adds MMCSS thread priority and IAudioClient3 low latency, and it
  exposes `IsFormatSupported` so the endpoint can be asked what it accepts rather than
  guessed at.

## Editing config.json by hand

The app writes the whole config on exit, from memory. So editing
`%APPDATA%\Patchboard\config.json` while Patchboard is running achieves nothing: the
running instance overwrites the file when it closes and your edit vanishes. Close the app
first, edit, then relaunch. This has already caught us once, and the symptom is confusing
because the edit is visibly correct on disk right up until the app exits.

The same mechanism means two instances fight, with the last one to close winning.

## Never touch the system volume

`WasapiOut.Volume`, and `WasapiPlayer.DeviceVolume`, write the **system-wide endpoint
volume**. Setting either changes the user's Windows audio settings for every application,
which this project is explicitly forbidden from doing. All gain in Patchboard is applied
inside our own mix, in `CachedSoundSampleProvider.Volume` and `OutputChannel.Volume`.
`WasapiPlayer` also offers `SessionVolume` and `StreamVolume`, which are per-stream and
safe, but the mix already handles it and adding a second place to set gain would only
create a way for the two to disagree.

## Known limits, deliberately accepted

- **The outputs will drift apart over time.** Every endpoint has its own crystal, so two
  devices playing the same clip diverge at a rate no start ordering can fix. Windows
  offers `IAudioClockAdjustment::SetSampleRate` for apps that must stay locked. Soundboard
  clips are seconds long, so the drift is inaudible and correcting it is not worth the
  complexity. Revisit only if long music beds become a real use case.
- **Start skew between devices is roughly one buffer period** plus thread scheduling,
  because each stream starts on its own engine period boundary. At the default 60ms
  buffer that is under a frame of video, and unnoticeable for a sound effect.
- Endpoint IDs survive reboots and USB replug but **change on driver update or device
  reinstall**, which is why `DeviceService.Resolve` falls back to the friendly name.
  Windows 11 24H2 added `PKEY_AudioEndpoint_StableId`, which is genuinely stable, but
  NAudio exposes no constant for it. Reading it would mean declaring the PropertyKey by
  hand, and not every endpoint has one. Not worth it until a device actually goes missing.

## Device identity

Persist `MMDevice.ID` (the endpoint ID string), never `FriendlyName`. Friendly names
change when Windows renames a device or a driver updates, and Resanance's own
`devices.txt` stores truncated names, which is part of why it loses bindings. Keep the
friendly name alongside it for display and for a readable fallback match if the ID is
gone.

## Verify before claiming it works

Run the checks. Close Patchboard first, because a running app locks its own exe and the
build fails with MSB3027.

    dotnet run --project tests/Patchboard.Checks

It covers config, decoding his real files, the whole playback chain in memory, device
enumeration and resolution, live WASAPI output, mic capture and routing, preview to a
single device, settings round tripping through a real save and load, hotkey parsing, both
import paths, the runtime footprint, and how a dead button reports itself. It prints
PASSED and FAILED counts and exits after reporting. Read the count off the run rather than
from here, because a number written down is stale the next time a check is added.

**Never run it with `--no-build`.** That silently executes the previous binary, so code
that does not compile still reports a pass. It happened once and the run looked clean.

The settings round trip writes to his real config file and restores it in a `finally`.
Any check added there must keep that guarantee, and the suite asserts the restore.

Two rules the checks themselves obey, and any addition to them must too:

- **They only ever open "Steam Streaming Speakers" for output**, an endpoint that is inert
  because Steam is not running. Never a device Samuel or his Discord call can hear.
- **They never open his real microphone.** The mic path is tested against "CABLE Output",
  the capture side of the virtual cable, which delivers silence unless something is
  playing into CABLE Input. Opening the eMeet or the USB mic would be recording him.

A test that measures silence proves nothing. The playback checks deliberately pick the
decoded clip with the loudest opening second, because the first attempt used "arana"
(Aria Math), which fades in from silence, and three checks failed for that reason alone
while the code was correct.

What the checks cannot cover, and is therefore Samuel's to confirm: that a sound is
audible on the devices HE selects, and anything behind a mouse click (rename dialog,
drag to reorder, the context menu, the colour swatches, the trim boxes).

**A running Patchboard holds its own exe open**, so `publish/` cannot be rebuilt while it is
running and his shortcuts keep pointing at the old build until he closes it and republishes.
Check with `Get-Process Patchboard` before claiming a fix has reached him.

    dotnet build src/Patchboard/Patchboard.csproj    # must exit 0 with no warnings

**Build it cold before believing a clean build.** A warm `obj/` hides the error that
`UseWPF` strips `System.IO` out of the implicit usings; delete `obj/` and `bin/` in both
projects and build again.

## Releasing

    dotnet publish src/Patchboard/Patchboard.csproj -p:PublishProfile=win-x64 -o publish

One self contained exe, no .NET install needed to run it. The settings live in
`src/Patchboard/Properties/PublishProfiles/win-x64.pubxml`, not in the csproj: putting
`SelfContained` in the project file makes the checks project fail to build outright with
NETSDK1151, because a self contained executable cannot be referenced by one that is not.

Measured on this project, not assumed:

| Setting | Size |
|---|---|
| single file, self contained | 156 MB |
| the above plus `EnableCompressionInSingleFile` | 66 MB |
| the above plus `InvariantGlobalization` | 66 MB, so it buys nothing and is not set |

`PublishTrimmed` is not an option. WPF is not trimmable; it builds and then fails at
runtime when XAML reflects over a type the trimmer removed.

## Never decode on the UI thread

`CachedSound.Load` is called from a button press. It used to run inline, on the UI thread,
and decode the whole file. Measured on the real library: pressing a 71 minute button locked
the window for **4393ms** and then reported a failure, because the length check only fired
once twenty minutes of audio had been decoded. A legitimate ten minute clip blocked for
1784ms.

Two rules came out of it, and both are checked in section 8d:

- **Read `AudioFileReader.TotalTime` before decoding anything.** Rejecting an over-long clip
  is a header read, not a decode. Worst case went from 4393ms to 4ms.
- **Decode on a background thread.** `MainViewModel.Decode` awaits `Task.Run`, and a clip
  already in `SoundCache` returns without awaiting at all so an ordinary press stays instant.

`SoundCache` is UI-thread-only, so a background decode is added to it after the await, never
from the worker.

**Trimming interacts with the length limit deliberately.** The limit applies to the window
kept, not the file it came from, which is what makes an hour long recording usable. Only a
trimmed clip stops at the header's reported length; an untrimmed one reads to end of stream,
because a VBR MP3 with no Xing header under-reports its own duration and stopping early
would silently cut the end off.

## Staying lightweight

This runs behind a game for hours. Two costs are easy to add by accident and neither has
a visible symptom until it is bad.

- **Mixer inputs are the render thread's per buffer workload.** Every input is read on
  every buffer, forever, and NAudio cannot remove one without the exact provider instance
  that was added. A mic sink that was dropped and recreated on each toggle left one dead
  reader behind per flick; thirty flicks measured thirty of them. `AudioEngine.ReadMixerLoad`
  exists to make that observable and section 8 of the checks asserts it stays at one.
- **The decoded cache is bounded and must stay bounded.** 48kHz stereo float is 384KB per
  second, so his 203 playable clips are 3.3GB if all of them are held. `SoundCache` keeps
  256MB, least recently used evicted first, and hands back anything larger than the whole
  budget without storing it.

The 60ms UI timer does no per button work when nothing is playing, and reads no meters
while the window is minimised. Both are on the path a soundboard actually spends its life
in.

## Where things are

    src/Patchboard/            the app
      Models/                  plain data types, serialised to JSON
      Services/                audio engine, devices, hotkeys, config, import
      ViewModels/              UI state
      Views/                   XAML
    DESIGN.md                  visual conventions
    %APPDATA%\Patchboard\      runtime config, not in the repo
