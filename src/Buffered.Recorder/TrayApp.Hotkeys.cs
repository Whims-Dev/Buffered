using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Text.Json;

namespace Buffered.Recorder;

/// <summary>The hotkeys, and saving a clip.</summary>
sealed partial class TrayApp
{
    // ---- hotkey, saving ----------------------------------------------------

    /// <summary>True while the hotkey (a key, or a key with Ctrl, Alt and Shift) is held. The modifiers must match exactly, so F9 and Ctrl+F9 can be different hotkeys.</summary>
    private static bool IsDown(string? hotkey)
    {
        int code = Settings.CodeFor(hotkey);
        if (code == 0 || Native.GetAsyncKeyState(code) >= 0) return false;
        int want = Settings.ModifiersFor(hotkey);
        int have = (Native.GetAsyncKeyState(0x11) < 0 ? 1 : 0) | (Native.GetAsyncKeyState(0x12) < 0 ? 2 : 0) | (Native.GetAsyncKeyState(0x10) < 0 ? 4 : 0);
        return want == have;
    }

    private void OnTick()
    {
        bool pressed = IsDown(_settings.Hotkey);
        if (pressed && !_keyDown) SaveClip();
        _keyDown = pressed;
        bool pp = IsDown(_settings.PauseHotkey), sp = IsDown(_settings.StopHotkey);
        bool pauseHit = pp && !_pauseKeyDown, stopHit = sp && !_stopKeyDown;
        _pauseKeyDown = pp; _stopKeyDown = sp;
        foreach (var ck in _settings.ClipKeys.ToArray())
        {
            bool down = IsDown(ck.Key);
            bool was = _clipKeysDown.Contains(ck);
            if (down && !was) { _clipKeysDown.Add(ck); SaveClip(Math.Max(1, ck.Seconds)); }
            else if (!down && was) _clipKeysDown.Remove(ck);
        }
        _clipKeysDown.RemoveWhere(k => !_settings.ClipKeys.Contains(k));
        if (stopHit) { SetStopped(!_stopped); return; }
        if (pauseHit) TogglePause();

        _ticks++;
        if (_ticks % 33 == 0) _lastApp = ForegroundApp.Current() ?? _lastApp;
        if (_ticks % 100 == 0) { CheckSteamVr(); CheckGameWindow(); }
        if (_ticks % 100 == 0 && _engine is { } e)
        {
            double shownSeconds = e.BufferSeconds > 0 ? Math.Min(e.BufferedSeconds, e.BufferSeconds) : e.BufferedSeconds;   // can briefly hold a moment more than its length
            int m = (int)shownSeconds / 60, s = (int)shownSeconds % 60;
            string head = e.IsPaused ? "Paused" : "Recording";
            _status.Text = $"{head} · {m}:{s:00} buffered";
            _tray.Text = e.IsPaused ? $"Buffered · paused · {m}:{s:00} buffered" : $"Buffered · {m}:{s:00} buffered";
        }
    }

    private bool _vrAtStart;
    private int _vrFlips;

    /// <summary>
    /// When SteamVR starts or stops, restart onto the matching audio devices. It has to be stable for two checks in a row
    /// (about six seconds) so SteamVR's start-up flicker doesn't cause repeated restarts.
    /// </summary>
    private void CheckSteamVr()
    {
        if (_stopped || _engine is null || !_settings.VrAutoSwitch) { _vrFlips = 0; return; }
        bool now = AudioDevices.IsSteamVrRunning();
        _vrFlips = now != _vrAtStart ? _vrFlips + 1 : 0;
        if (_vrFlips >= 2) Relaunch(false);
    }

    private int _targetFlips;

    /// <summary>
    /// Every few seconds: keep a minimized game window restored (a minimized window can't be recorded), and switch between recording
    /// the monitor and recording the game's window when the game starts or closes. It has to differ twice in a row (about six seconds)
    /// so a game that is still loading its window doesn't cause a restart.
    /// </summary>
    private void CheckGameWindow()
    {
        var engine = _engine;
        if (_stopped || engine is null) { _targetFlips = 0; return; }

        var want = GameWindow.Find(_settings);
        var profile = _settings.ProfileFor(want?.ProcessName);
        bool windowOnly = want is not null && (profile?.WindowOnly ?? true);
        if (windowOnly && _settings.KeepWindowVisible) GameWindow.EnsureVisible(want!.Handle);

        string wantKey = windowOnly ? $"win:{want!.ProcessId}" : "monitor";
        string wantProfile = profile is { IsEdited: true } ? Settings.GameKey(want!.ProcessName) : "";
        // On the right source (or already tried it and fell back), and using the right game's settings.
        bool settled = (wantKey == engine.SourceKey || wantKey == engine.FallbackKey) && wantProfile == engine.ProfileKey;
        _targetFlips = settled ? 0 : _targetFlips + 1;
        if (_targetFlips >= 2) Relaunch(false);

        // The program on its own track: recording restarts when it starts or stops (or is replaced by a new copy), because the
        // Game track changes from the whole device to "everything except that program" and back.
        var wantApp = RecorderEngine.FindApp(_settings);
        string wantAppKey = wantApp is { } w ? $"{Settings.GameKey(w.Name)}:{w.Pid}" : "";
        _appFlips = wantAppKey != engine.AppKey ? _appFlips + 1 : 0;
        if (_appFlips >= 2) Relaunch(false);
    }

    private int _appFlips;

    private string? DoSave(Action? onCaptured = null, double? lastSeconds = null)
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;
        if (e is null) throw new InvalidOperationException(_stopped ? "Recording is stopped." : _error ?? "The recorder is not running.");
        // A recorded game names its own clips; otherwise the clip is named after the program that was in front.
        string? label = e.GameName is { Length: > 0 } g ? ClipNames.FriendlyName(g) : (ForegroundApp.Current() ?? _lastApp);
        return _lastClip = e.SaveClip(label, lastSeconds, onCaptured);
    }

    private void SaveClip(double? lastSeconds = null)
    {
        if (_saving) return;
        _saving = true;
        Task.Run(() =>
        {
            try
            {
                // The sound plays the moment the clip's contents are captured (a fraction of a second after the key press),
                // not after the file is finished, and after the capture so it can't end up in the clip.
                DoSave(() => { if (_settings.SaveSound) Sounds.PlaySaved(); }, lastSeconds);
                Flash(_iconSaved);
            }
            catch
            {
                if (_settings.SaveSound) Sounds.PlayFailed();
                Flash(_iconFailed);
            }
            finally { _saving = false; }
        });
    }

    /// <summary>
    /// Quiet feedback in the tray: the icon's status dot briefly turns green (saved) or red (failed).
    /// Buffered deliberately never raises a Windows notification, because those can interrupt full-screen games.
    /// </summary>
    private void Flash(Icon icon)
    {
        _sync.BeginInvoke(() => { if (!_quit) _tray.Icon = icon; });
        Task.Delay(1300).ContinueWith(_ => _sync.BeginInvoke(() =>
        {
            if (!_quit) _tray.Icon = _engine is not null && !_stopped && !_engine.IsPaused ? _iconOn : _iconBusy;
        }));
    }

}
