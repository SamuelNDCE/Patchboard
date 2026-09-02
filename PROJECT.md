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

NAudio 3.0 notes, verified against the installed assembly on 2026-09-02, not from docs:
- The package is split. `NAudio.Wasapi` holds `WasapiOut`, `WasapiCapture`,
  `MMDeviceEnumerator`. `NAudio.Core` holds the sample providers.
- `MediaFoundationResampler` is NOT present in 3.0.1. Use `WdlResamplingSampleProvider`.
  Any guide telling you otherwise is written for NAudio 2.x.
- `WasapiOut(MMDevice, AudioClientShareMode, bool useEventSync, int latencyMs)` is the
  constructor that targets a specific device.

## Device identity

Persist `MMDevice.ID` (the endpoint ID string), never `FriendlyName`. Friendly names
change when Windows renames a device or a driver updates, and Resanance's own
`devices.txt` stores truncated names, which is part of why it loses bindings. Keep the
friendly name alongside it for display and for a readable fallback match if the ID is
gone.

## Verify before claiming it works

1. `dotnet build src/Patchboard/Patchboard.csproj` exits 0 with no warnings.
2. Device enumeration lists real endpoints, checked against
   `Get-PnpDevice -Class AudioEndpoint -Status OK`.
3. The app launches and the window renders. Screenshot it.
4. Sound playback is verified BY SAMUEL, not by us. See the hard rules.

## Where things are

    src/Patchboard/            the app
      Models/                  plain data types, serialised to JSON
      Services/                audio engine, devices, hotkeys, config, import
      ViewModels/              UI state
      Views/                   XAML
    DESIGN.md                  visual conventions
    %APPDATA%\Patchboard\      runtime config, not in the repo
