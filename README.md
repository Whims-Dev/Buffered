# Buffered

A lightweight instant-replay clipper for Windows. It keeps the last few minutes of your screen and audio in RAM, and when you press a hotkey it saves them as an MP4. It was built because other clippers hold close to 1 GB of VRAM while a game is open; Buffered sits around 100 MB.

- **Low VRAM.** Capture, scaling and H.264 encoding all stay on the GPU. The replay buffer lives in system RAM (about 370 MB for 5 minutes at 720p60).
- **Clip manager.** A small window to browse clips, play them, and trim them. It can stay open without costing much.
- **Audio.** Game audio and microphone are recorded together and can be re-balanced per clip afterwards.
- **Game windows.** Add a game and Buffered records just its window instead of the whole monitor, even if you minimize it (it is restored behind your other windows).
- **Clip by voice.** Say a phrase like "clip that" to save a clip. It runs on Windows' built-in speech recognizer, entirely on your PC, and only works with an English (US) Windows speech recognizer installed. Off by default.
- **Clip manager extras.** Folders, favorites (protected from delete), names and notes, drag-select, copy and drag a clip out to share.
- **Quiet.** No Windows notifications, so it won't interfere with fullscreen games.
- **SteamVR aware.** While SteamVR runs, it records the Steam Streaming audio devices instead.

## Download

Get `Buffered-win-x64.zip` from the [Releases](../../releases) page, unzip it anywhere, and run `BufferedUI.exe`. Everything it needs (including .NET and ffmpeg) is in the zip.

You need Windows 10 or 11 (64-bit) and a GPU with a hardware H.264 encoder (NVIDIA NVENC, AMD AMF or Intel QSV). Only an NVIDIA GPU has been tested so far; AMD and Intel use the same code path but are unverified, and bug reports are welcome.

If you build from source instead, you also need [ffmpeg](https://ffmpeg.org/) (`ffmpeg.exe` and `ffprobe.exe`) on your PATH or next to `BufferedUI.exe`. Easiest: `winget install Gyan.FFmpeg`.

## Using it

1. Run `BufferedUI.exe`. It opens the clip manager and starts the recorder in the system tray.
2. Press **F8** (change it in Settings) to save the last few minutes. Clips go to `Videos\Buffered`.
3. Open a clip to play it, trim it, or change the game/microphone balance.
4. In Settings, use **Add game** to record only a game's window.

Closing the window leaves the recorder running in the tray. Use the tray icon to pause (this frees its VRAM) or quit.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Buffered.slnx -c Release
```

To make a self-contained folder and zip that runs without .NET installed:

```powershell
powershell -File scripts\publish.ps1 -FfmpegDir C:\path\to\ffmpeg\bin
```

Leave out `-FfmpegDir` to ship without ffmpeg. If you bundle it, check that ffmpeg build's license first.

Pushing a tag such as `v0.1.0` runs the release workflow, which downloads a checksum-verified ffmpeg build, builds the zip and attaches it to a GitHub release.

## How it works

Windows Graphics Capture feeds frames to a Direct3D 11 video processor, which scales them and converts to NV12 on the GPU. A Media Foundation hardware H.264 encoder compresses them, and the packets go into a RAM ring buffer along with audio from WASAPI. Saving pipes the buffer through ffmpeg to mux an MP4 without re-encoding the video.

Windows Graphics Capture leaks a small amount of memory for each capture session, so the recorder restarts itself in place when settings change or a capture has to be recreated.

## Known limits

- Games with anti-cheat may block window capture; monitor capture still works.
- Changing the recorded monitor or game, or resizing a recorded window, restarts recording and empties the buffer.
- The recorder and clip manager are two small programs that talk over a named pipe.

## License

[MIT](LICENSE)
