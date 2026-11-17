# Buffered

A lightweight instant-replay clipper for Windows. It keeps the last few minutes of your screen and audio in RAM, and when you press a hotkey it saves them as an MP4. It was built because other clippers hold close to 1 GB of VRAM while a game is open; Buffered sits around 100 MB.

- **Low VRAM.** Capture, scaling and H.264 encoding all stay on the GPU. The replay buffer lives in system RAM (about 370 MB for 5 minutes at 720p60).
- **Clip manager.** A small window to browse clips, play them, and trim them. It can stay open without costing much.
- **Editor.** A timeline that shows the part you keep in a gold frame, with the clip's sound, little pictures from it, or both, plus the times. Trim with the handles or by typing times, step a frame at a time with `,` and `.`, and jump with the arrow keys (the distances are settings). Moving the playhead stays smooth, because Buffered quietly makes a copy of the clip with frequent keyframes in the background while you work.
- **Interface zoom.** Hold Ctrl and turn the mouse wheel, or press Ctrl and plus or minus, to make everything bigger or smaller (Ctrl 0 goes back to 100%).
- **Audio.** Game audio and microphone are recorded together, with a volume and a mute button for each track in the editor. One app, such as Discord, can be recorded on a track of its own, so you can turn it up, down or off in a clip later.
- **Game windows.** Add a game and Buffered records just its window instead of the whole monitor, even if you minimize it (it is restored behind your other windows). Each game can have its own quality, buffer length, volumes and clips folder.
- **Sharing.** Make a smaller copy that fits a chat app (10, 20, 25 or 50 MB), copy the original, or post a clip straight to a Discord channel through a webhook, with a title, details and the video attached. Keep as many named, colored webhooks as you like and put them in your own order. Everything happens when you click; nothing is uploaded on its own.
- **Folders and favorites.** Folders (each with its own color), favorites that are protected from delete, names and notes, drag-select, copy and drag a clip out.
- **Easy settings.** Settings are split into sections with a search box, and Buffered can start with Windows and add its own shortcuts. Volumes are plain percentages (100% is the sound as recorded).
- **Updates when you ask.** In Settings, Updates, press Check for updates, then Download, then Restart to update. The download is checked against the hash GitHub publishes for it, the old files are put back if anything goes wrong, and nothing is checked or installed on its own.
- **Quiet.** No Windows notifications, so it won't interfere with fullscreen games.
- **SteamVR aware.** While SteamVR runs, it records the Steam Streaming audio devices instead.

## Download

Get `Buffered-win-x64.zip` from the [Releases](../../releases) page, unzip it anywhere, and run `BufferedUI.exe`. Everything it needs (including .NET and ffmpeg) is in the zip.

You need Windows 10 or 11 (64-bit) and a GPU with a hardware H.264 encoder (NVIDIA NVENC, AMD AMF or Intel QSV). Only an NVIDIA GPU has been tested so far; AMD and Intel use the same code path but are unverified, and bug reports are welcome.

If you build from source instead, you also need [ffmpeg](https://ffmpeg.org/) (`ffmpeg.exe` and `ffprobe.exe`) on your PATH or next to `BufferedUI.exe`. Easiest: `winget install Gyan.FFmpeg`.

### Windows may warn you

Buffered is not code-signed, because certificates cost money and this is a free hobby project. The first time you run it, Windows SmartScreen may say **"Windows protected your PC"**. That is Windows being careful about any program it has not seen many times, not a sign that something is wrong with it. To run it, click **More info**, then **Run anyway**.

You can avoid the warning by right-clicking the downloaded zip, choosing **Properties**, ticking **Unblock** and pressing OK *before* you unzip it. On Windows 11 with **Smart App Control** turned on, unsigned programs can be blocked outright; in that case Buffered will not run unless you turn Smart App Control off, which is your call to make.

You do not have to take anyone's word that it is safe: the full source is in this repository, and you can build it yourself (see [Building](#building)). Releases are meant to be built by the GitHub workflow in `.github/workflows/release.yml`, so the zip comes from the code you can read here.

## Using it

1. Run `BufferedUI.exe`. It opens the clip manager and starts the recorder in the system tray.
2. Press **F8** (change it in Settings) to save the last few minutes. Add as many extra hotkeys as you like in Settings, each saving just the last few seconds or minutes you choose. Clips go to `Videos\Buffered`.
3. Open a clip to play it, trim it, or change the volume of each track. Select a clip and press **Share** to make a smaller copy or send it to Discord.
4. In Settings, use **Add game** to record only a game's window, and **Sharing** to add a Discord webhook.

Closing the window leaves the recorder running in the tray. **Pause** holds recording and keeps what is in the buffer (a clip saved later joins what came before and after). **Stop** turns recording off, empties the buffer and frees its VRAM. Both are in the window and the tray menu, and each can have an optional hotkey in Settings (the ✕ clears it).

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

## A note from the author

> I created Buffered because most clipping software I've tried is incredibly resource intensive. I have a GPU with only 8 GB of VRAM, and I can't spare a gigabyte for one process.
>
> Life is beautiful, and clipping software is a great tool for capturing the great moments.

## Built with AI

Buffered was written with a lot of help from an AI coding assistant (Claude, made by Anthropic), following the author's design and feedback. The author directs what it does and tests it by using it, but the code has not had an independent review, and it has been tried on one PC (Windows 11, NVIDIA GPU). Expect rough edges and bugs, especially on other hardware.

It is provided as is, with no warranty (see the license). If something looks wrong or unsafe, please [open an issue](../../issues).

## License

[MIT](LICENSE)
