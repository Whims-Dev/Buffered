using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>
/// The live level shown inside a volume slider (style "MeterSlider"), like Windows' own volume mixer: a bright bar along the track that
/// rises and falls with the sound, over the fill that runs up to the knob, and can only reach as far as the knob. The end turns red when the sound reaches full scale.
/// </summary>
internal sealed class SliderMeter
{
    private const double FloorDb = -60;       // the left end of the bar
    private const double FallDbPerSecond = 45;
    private const double KnobHalf = 10;       // half the knob's width: the bar stops before it

    private readonly Slider _slider;
    private Border? _bar, _fill;
    private double _db = FloorDb;
    private long _at = Stopwatch.GetTimestamp();
    private bool _hot, _shownHot, _styledOnce;

    private SliderMeter(Slider slider)
    {
        _slider = slider;
        slider.Loaded += (_, _) => Find();
        slider.SizeChanged += (_, _) => Draw();
        slider.ValueChanged += (_, _) => Draw();
        if (slider.IsLoaded) Find();
    }

    public static SliderMeter Attach(Slider slider) => new(slider);

    private void Find()
    {
        slider_ApplyTemplate();
        _bar = _slider.Template?.FindName("PART_Meter", _slider) as Border ?? FindBar(_slider, "PART_Meter");
        _fill = _slider.Template?.FindName("PART_Fill", _slider) as Border ?? FindBar(_slider, "PART_Fill");
        Draw();
    }

    private static Border? FindBar(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Border b && b.Name == name) return b;
            if (FindBar(c, name) is { } inner) return inner;
        }
        return null;
    }

    private void slider_ApplyTemplate() { try { _slider.ApplyTemplate(); } catch { } }

    /// <summary>Feeds the loudest sample (0 to 1, after the volume is applied) since the last call. Call it every frame or two, also with 0 while nothing plays.</summary>
    public void Push(float peak)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_at, now).TotalSeconds; _at = now;
        double db = peak > 0.00001f ? 20 * Math.Log10(peak) : FloorDb;
        _db = db >= _db ? db : Math.Max(db, _db - FallDbPerSecond * dt);   // rises at once, falls steadily
        _hot = peak >= 0.98f || (_hot && _db > -1.5);
        Draw();
    }

    private void Draw()
    {
        if (_bar is null) return;
        double w = _slider.ActualWidth;
        double span = _slider.Maximum - _slider.Minimum;
        if (w <= 0 || span <= 0) { _bar.Width = 0; if (_fill is not null) _fill.Width = 0; return; }
        double frac = Math.Clamp((_slider.Value - _slider.Minimum) / span, 0, 1);
        double knobCenter = KnobHalf + frac * (w - 2 * KnobHalf);
        if (_fill is not null) _fill.Width = Math.Round(knobCenter);   // the fill behind the knob, as in any slider
        double room = knobCenter;                                      // the bar can reach the knob's middle (it runs behind the fill and the knob)
        double level = Math.Clamp((_db - FloorDb) / -FloorDb, 0, 1);   // 0 at -60 dB, 1 at full scale
        _bar.Width = Math.Round(room * level);
        if (_hot != _shownHot || _bar.Background is null || !_styledOnce)
        {
            _bar.Background = _hot ? HotBrush : LiveBrush;
            _shownHot = _hot; _styledOnce = true;
        }
    }

    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));   // white and a little thicker than the fill, so it stands out against the accent-colored fill behind the knob
    private static readonly Brush HotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
