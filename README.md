# Patchboard

A Windows soundboard that plays to several audio devices at once, so a clip can reach your
headphones and a voice chat in the same press.

Built because most soundboards play to exactly one output. That is the wrong shape for the job:
you want to hear the sound yourself *and* have it arrive in Discord, and on a machine with a
capture card or a second mic you may want it in more places than that. Patchboard decodes a clip
once and fans the same audio out to every device you tick.

This started as a personal project to replace a soundboard that had stopped working right on
my own machine, and it's shared here in case it's useful to anyone else who wants one. It's a
single self-contained executable, no installer, no background service, no telemetry, and it's
open source under MIT, so you can read every line it runs.

## The part everyone gets wrong

**A soundboard cannot put sound into Discord on its own.** Windows has no way for an app to
"speak into" your microphone. What actually happens is:

```
Patchboard  ->  a virtual audio cable  ->  Discord listens to that cable as its microphone
```

So you need a virtual cable installed, and you need the voice app pointed at it. Without one,
Patchboard plays to your speakers and nobody else hears a thing. That failure is silent and looks
exactly like the app working, which is why Patchboard warns about it explicitly and offers to fix
the routing in one click.

Either of these works, both are free:

- [VB-Cable](https://vb-audio.com/Cable/). Simplest, installs one cable.
- [VoiceMeeter](https://vb-audio.com/Voicemeeter/). A full mixer, more setup and more control.

Patchboard does not install either of these for you: they install an audio driver, which is not
something an app should do silently. Download and install one from the links above, then relaunch
Patchboard, and it will find it and route to it on its own. If it doesn't, the amber banner's
**Add a cable** button will pick it up as soon as one is enabled in Windows' Sound settings.

## Requirements

- Windows 10 or later, 64-bit. The dark title bar needs 1809. Below that it is light and everything else works
- A virtual audio cable, if you want anyone else to hear anything
- Nothing else. The release build is self-contained, so no .NET install is needed

## Getting started

1. Run `Patchboard.exe`.
2. On first launch it picks a sensible route on its own: your default playback device, so you
   hear the sounds, plus a virtual cable if one is installed, so other people do. Check the
   OUTPUT panel on the left and adjust if it guessed wrong.
3. Point your voice app's microphone at the same cable. In Discord that is
   User Settings, Voice & Video, Input Device.
4. Drag audio files onto the window, or use **Add sounds**.
5. Click a button to play it. Click it again to stop.

If the amber "No virtual cable selected" banner is showing, sound is only reaching you. Press
**Add a cable** and it will pick the right one.

### If you use push to talk

Your sounds will not transmit unless the channel is open, so hold your talk key while the clip
plays. Settings has a **Delay before playing** so the clip starts after the channel opens rather
than losing its first word, and any single sound can override that.

## What it does

**Output**

- Play to any number of devices at once, each with its own volume
- Mark one device as "my headphones" and preview a sound there only, so you can audition
  something without sending it to everyone
- Optional microphone passthrough, per output device, off by default
- A **Mute mic** panic button, and a warning when your routing would create a feedback loop

**Sounds**

- `.mp3` `.wav` `.ogg` `.flac` `.m4a` `.aac` `.wma` `.aiff` `.aif`
- Per-sound volume from 0 to 200%, so a quiet recording can be boosted
- Trim a clip to just the part you want, on a two-handle slider over its real length
- Any clip length. Long recordings stream from disk instead of being decoded into memory
- Seek through whatever is playing from the transport bar
- Global hotkeys that still fire while the window is minimised and a game has focus
- Colour a button, give it an image, rename it, drag to reorder

**Library**

- Import a folder in bulk
- Tidy up labels, which strips the artefacts bulk downloads leave behind: `(1)` suffixes, ripper
  site prefixes, download ids, editor leftovers. It never touches a name you wrote yourself
- Find duplicates, confirmed by content hash rather than file size
- Remove buttons whose file has gone

## Settings

| Setting | What it does |
|---|---|
| Columns / Rows visible | Grid size. Anything past the visible rows scrolls, so no sound is hidden |
| Audio buffer | WASAPI buffer, 20 to 500 ms. Lower is snappier, too low crackles under load |
| Delay before playing | Lead-in before a clip starts, for push to talk. Any sound can override it |

Settings live in `%APPDATA%\Patchboard\config.json` and are written atomically on every change.
A corrupt file is moved aside rather than deleted, and the app still opens.

## Building from source

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
dotnet build src/Patchboard/Patchboard.csproj -c Release
```

Release build, one self-contained file:

```
dotnet publish src/Patchboard/Patchboard.csproj -p:PublishProfile=win-x64 -o publish
```

### Checks

```
dotnet run --project tests/Patchboard.Checks
```

A console harness rather than a unit test framework, because most of what is worth checking here
is real audio behaviour: decoding, resampling, the mixer, opening actual WASAPI endpoints,
capture, routing safety, and settings surviving a round trip.

Two things to know before running it:

- **It writes your real `config.json`** in a few places and restores it afterwards. It keeps a
  rescue copy on disk while it does, but killing the run at the wrong moment can still cost you
  your library. Back it up first if it matters.
- It opens live audio devices. It only ever targets endpoints that are inert on the development
  machine, so on yours it may open something you can hear.

## How it works

- **NAudio 3.0.1**, WASAPI in shared mode. Exclusive mode would seize the endpoint and cut off
  the game and the voice app
- One output channel per selected device, each holding its own mixer and stream open for the
  life of the selection. Opening a WASAPI stream costs tens of milliseconds, which on a
  soundboard is the difference between landing a joke and missing it
- Everything is converted to one canonical format, 48 kHz stereo float, before it reaches a mixer
- A clip is decoded once and shared. Each device gets a cheap reader over the same array, so the
  outputs cannot drift apart
- Clips under two minutes stay in memory under a 256 MB budget, least recently used evicted
  first. Longer ones stream from disk, which is what removes any length limit
- Decoding happens off the UI thread. Global hotkeys use `RegisterHotKey` rather than a
  low-level keyboard hook, deliberately: a hook sits in the input path of every keystroke on the
  machine and is the exact signature anti-cheat looks for

## Licence

MIT. See [LICENSE](LICENSE).

NAudio is MIT licensed.
