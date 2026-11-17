using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Text.Json;

namespace Buffered.Recorder;

/// <summary>The pipe the clip manager window talks to.</summary>
sealed partial class TrayApp
{
    // ---- pipe server (clip manager window talks to this) ------------------

    private void PipeServer()
    {
        while (!_quit)
        {
            try
            {
                var pipe = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                try { pipe.WaitForConnection(); } catch { pipe.Dispose(); throw; }
                var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                string? command;
                try { command = reader.ReadLine(); } catch { pipe.Dispose(); throw; }
                if (command == "save")
                {
                    // A save can take a while (a slow or network drive), so it runs on its own and the window's status polls still get answers.
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            string r; try { r = SaveFromPipe(); } catch (Exception ex) { r = "error: " + ex.Message; }
                            writer.WriteLine(r);
                        }
                        catch { /* the window went away */ }
                        finally { pipe.Dispose(); }
                    });
                    continue;
                }
                using var _p = pipe;
                string reply;
                try
                {
                    reply = command switch
                    {
                        "status" => JsonSerializer.Serialize(BuildStatus(), Ipc.Json),
                        "levels" => JsonSerializer.Serialize(new RecorderLevels(LevelsNow()), Ipc.Json),
                        "reload" => ReloadFromPipe(),
                        "soft" => SoftFromPipe(),
                        "stop" => StopFromPipe(true),
                        "start" => StopFromPipe(false),
                        "pause" => PauseFromPipe(true),
                        "resume" => PauseFromPipe(false),
                        _ => "error: unknown command",
                    };
                }
                catch (Exception ex) { reply = "error: " + ex.Message; }
                writer.WriteLine(reply);
            }
            catch (IOException) { /* client went away */ }
            catch (Exception) when (_quit) { return; }
            catch { Thread.Sleep(200); }
        }
    }

    private string SaveFromPipe()
    {
        if (_saving) return "error: a save is already in progress";
        _saving = true;
        try { return DoSave(() => { if (_settings.SaveSound) Sounds.PlaySaved(); })!; } finally { _saving = false; }
    }

    private string ReloadFromPipe() { RelaunchSoon(_stopped); return "ok"; }

    /// <summary>
    /// Picks up the settings that can change while recording without a restart, so adjusting a volume
    /// doesn't throw away the buffer. The engine reads these from the same Settings object, so it sees them immediately.
    /// </summary>
    private string SoftFromPipe()
    {
        var s = Settings.Load();
        _settings.GameGainDb = s.GameGainDb;
        _settings.MicGainDb = s.MicGainDb;
        _settings.SeparateTracks = s.SeparateTracks;
        _settings.SaveSound = s.SaveSound;
        _settings.KeepWindowVisible = s.KeepWindowVisible;
        _settings.AppTracks = s.AppTracks;   // an app's volume and whether it is in the mix apply to the next clip
        _settings.GameProfiles = s.GameProfiles;   // a game's volume and folder apply to its next clip
        _settings.PauseHotkey = s.PauseHotkey;
        _settings.StopHotkey = s.StopHotkey;
        _settings.ClipKeys = s.ClipKeys;
        if (_settings.Hotkey != s.Hotkey)
        {
            _settings.Hotkey = s.Hotkey;
            _sync.BeginInvoke(() => _saveItem.Text = $"Save clip ({_settings.Hotkey})");
        }
        return "ok";
    }

    private string StopFromPipe(bool pause)
    {
        if (pause != _stopped) RelaunchSoon(pause);
        return "ok";
    }

    private Dictionary<string, float> LevelsNow()
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;
        return e?.MeterLevels() ?? new Dictionary<string, float>();
    }

    private string PauseFromPipe(bool pause)
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;
        if (_stopped || e is null) return "error: recording is not running";
        if (e.IsPaused != pause) _sync.BeginInvoke(() => { e.SetPaused(pause); ShowPaused(pause, e); });
        return "ok";
    }

    private RecorderStatus BuildStatus()
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;

        var now = DateTime.UtcNow;
        if ((now - _cpuPrevAt).TotalSeconds >= 1)
        {
            _self.Refresh();
            var cpu = _self.TotalProcessorTime;
            _cpuPct = (cpu - _cpuPrev).TotalSeconds / (now - _cpuPrevAt).TotalSeconds / Environment.ProcessorCount * 100;
            _cpuPrev = cpu; _cpuPrevAt = now;
        }
        if ((now - _vramAt).TotalSeconds >= 3) { _vram = Telemetry.OwnVramMb(); _vramAt = now; }

        return e is null
            ? new RecorderStatus(false, 0, _settings.BufferSeconds, 0, _vram, _cpuPct, 0, 0, 0, "", _error, false, Starting: _starting, Stopped: _stopped)
            : new RecorderStatus(true, e.BufferedSeconds, e.BufferSeconds, (e.BufferedBytes + e.AudioBytes) / 1048576.0, _vram, _cpuPct,
                                 e.OutputWidth, e.OutputHeight, e.Fps, e.Monitor?.ToString() ?? "", null, e.IsPaused, e.AudioDescription, e.GameLevel, e.MicLevel, e.SourceName, false, false);
    }

}
