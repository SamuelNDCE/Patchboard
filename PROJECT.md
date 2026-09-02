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

  What is still allowed: launching the app, `GetWindowRect` plus `CopyFromScreen` to
  capture **only the app's own window rectangle**, and read-only UI Automation queries
  scoped to the Patchboard process. What is not: `InvokePattern.Invoke`, `TogglePattern`,
  `mouse_event`, `SendKeys`, `SetCursorPos`, `SetForegroundWindow`, and full-screen
  capture, which photographs whatever he is actually doing.

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

44 checks covering config, decoding his real files, the whole playback chain in memory,
device enumeration and resolution, live WASAPI output, mic capture, hotkey parsing, and
both import paths. It prints PASSED and FAILED counts and exits after reporting.

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
drag to reorder, the context menu).

    dotnet build src/Patchboard/Patchboard.csproj    # must exit 0 with no warnings

## Where things are

    src/Patchboard/            the app
      Models/                  plain data types, serialised to JSON
      Services/                audio engine, devices, hotkeys, config, import
      ViewModels/              UI state
      Views/                   XAML
    DESIGN.md                  visual conventions
    %APPDATA%\Patchboard\      runtime config, not in the repo
