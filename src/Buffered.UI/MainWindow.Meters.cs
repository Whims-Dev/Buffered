using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>The live level inside the volume sliders in Settings, taken from what the recorder is hearing right now.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };   // 40 times a second, ahead of ordinary work, so the bars move smoothly
    private SliderMeter? _meterGame, _meterMic, _meterGame2, _meterMic2, _meterApp;
    private bool _meterBusy;
    private double _barGame, _barMic;
    private string _recSource = "";   // what the recorder is recording right now: "Monitor" or a game's name

    private void StartMeters()
    {
        _meterGame = SliderMeter.Attach(GameGainSlider);
        _meterMic = SliderMeter.Attach(MicGainSlider);
        _meterGame2 = SliderMeter.Attach(GameGainSlider2);
        _meterMic2 = SliderMeter.Attach(GameMicSlider2);
        _meterApp = SliderMeter.Attach(AppGainSlider);
        _meterTimer.Tick += async (_, _) => await MeterTickAsync();
        _meterTimer.Start();
    }

    private async Task MeterTickAsync()
    {
        if (_meterBusy) return;
        bool shown = IsVisible && WindowState != WindowState.Minimized;
        bool watching = shown && NavSettings.IsChecked == true;
        bool barMeters = shown && AudioMeters.Visibility == Visibility.Visible;   // the Game and Mic bars in the status strip
        if (!watching && !barMeters) { PushMeters(0, 0, 0, 0, 0); return; }
        _meterBusy = true;
        try
        {
            var lv = await Ipc.GetLevelsAsync();
            if (lv is null) { PushMeters(0, 0, 0, 0, 0); return; }
            float Of(string name) => lv.Tracks.TryGetValue(name, out var v) ? v : 0;
            float game = Of("Game"), mic = Of("Mic");
            if (barMeters) { _barGame = Math.Max(ToMeter(game), _barGame * 0.9); _barMic = Math.Max(ToMeter(mic), _barMic * 0.9); GameMeter.Value = _barGame; MicMeter.Value = _barMic; }   // rises at once, falls away smoothly
            if (!watching) return;
            float app = _selectedApp is { } a ? lv.Tracks.FirstOrDefault(kv => string.Equals(kv.Key, a, StringComparison.OrdinalIgnoreCase)).Value : 0;
            bool gameIsLive = _selectedGame is { } g && string.Equals(g, _recSource, StringComparison.OrdinalIgnoreCase);   // a game's own sliders only show sound while that game is being recorded
            PushMeters(game * (float)(GameGainSlider.Value / 100), mic * (float)(MicGainSlider.Value / 100),
                       gameIsLive ? game * (float)(GameGainSlider2.Value / 100) : 0, gameIsLive ? mic * (float)(GameMicSlider2.Value / 100) : 0,
                       app * (float)(AppGainSlider.Value / 100));
        }
        finally { _meterBusy = false; }
    }

    private void PushMeters(float game, float mic, float game2, float mic2, float app)
    {
        _meterGame?.Push(game); _meterMic?.Push(mic); _meterGame2?.Push(game2); _meterMic2?.Push(mic2); _meterApp?.Push(app);
    }
}
