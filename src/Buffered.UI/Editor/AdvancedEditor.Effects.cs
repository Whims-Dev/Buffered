using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>
/// Effect lanes: items that do not hold footage but act on the whole picture beneath them for a stretch of time (a transition over a cut, a shake).
/// This is the editor's side: adding them, their properties, and showing them on the preview. The export side is in <see cref="ExportGraph"/>.
/// </summary>
public partial class AdvancedEditor
{
    private const double ShakeSide = ExportGraph.ShakeSideShare, ShakeZoom = ExportGraph.ShakeZoomShare;

    private void InitEffects()
    {
        foreach (var (_, name) in Effects.All) EffectBox.Items.Add(name);
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#FF3B3B", "#FFD84D", "#4D9DFF", "#6BCB77", "#C77DFF" })
        {
            var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand, Tag = hex,
                                  Background = (Brush)new BrushConverter().ConvertFromString(hex)!, BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), BorderThickness = new Thickness(1) };
            sw.MouseLeftButtonDown += (_, _) => ApplyToSelection(o => o with { Color = hex, Effect = Effects.Canon(o.Effect) }, "transition colour");
            EffectSwatches.Children.Add(sw);
        }
    }

    /// <summary>Ready-made transitions: each is just a set of values for the same properties.</summary>
    private static readonly (string Name, string Colour, double Peak, double Hold, double Seconds)[] TransitionPresets =
    {
        ("Dip to black", "#000000", 0.5, 0, 1.0),
        ("Dip to white", "#FFFFFF", 0.5, 0, 1.0),
        ("White flash", "#FFFFFF", 0.15, 0, 0.4),
        ("Blackout (holds in the dark)", "#000000", 0.5, 0.4, 2.0),
    };

    /// <summary>Puts an effect on an effect lane (the first with room, or a new one) and selects it.</summary>
    private void AddEffectItem(string type, double start, double length, ItemProps? props = null)
    {
        start = Math.Max(0, start);
        var item = new TimelineItem(Guid.NewGuid(), ItemKind.Effect, "", 0, length, start, props ?? new ItemProps(Effect: type), null, Effects.NameOf(type));
        _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Effect, item, true, "Effects"));
        AfterEdit();
        Tl.SetSelection(new[] { item.Id }); _selected = item.Id; ShowProps(); RenderOverlay();
        StatusText.Text = Effects.IsTransition(type) ? "Transition added over the cut: drag it or pull its edges to move it or change its length; its properties are on the right" : "Shake added at the playhead: drag it or pull its edges to move it or change its length";
    }

    /// <summary>A transition centred on a cut (the selected one, or the nearest to the playhead, or the playhead itself).</summary>
    private void AddTransition((string Name, string Colour, double Peak, double Hold, double Seconds) preset)
    {
        double at = CutTime() ?? _t;
        AddEffectItem(Effects.Transition, Math.Max(0, at - preset.Seconds / 2), preset.Seconds, new ItemProps(Effect: Effects.Transition, Color: preset.Colour, Peak: preset.Peak, Hold: preset.Hold));
    }

    private void EffectAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string type }) return;
        if (Effects.IsTransition(type)) AddTransition(TransitionPresets[0]); else AddEffectItem(type, _t, 0.5);
    }

    private void EffectType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_uiBusy || EffectBox.SelectedIndex < 0) return;
        string type = Effects.All[EffectBox.SelectedIndex].Id;
        ApplyToSelection(o => o with { Effect = type }, "effect");
        ShowProps();
    }

    private void EffectAmount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        EffectAmountValue.Text = $"{EffectAmountSlider.Value:0}%";
        if (_uiBusy) return;
        ApplyToSelection(o => o with { Amount = EffectAmountSlider.Value / 100 }, "effect strength");
    }

    private void EffectShape_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PeakValue.Text = $"{PeakSlider.Value:0}% in"; HoldValue.Text = $"{HoldSlider.Value:0}%";
        if (_uiBusy) return;
        ApplyToSelection(o => o with { Peak = PeakSlider.Value / 100, Hold = HoldSlider.Value / 100, Effect = Effects.Canon(o.Effect) }, "transition shape");
    }

    /// <summary>Fills in the effect part of the properties panel.</summary>
    private void ShowEffectProps(TimelineItem it)
    {
        EffectBox.SelectedIndex = Array.FindIndex(Effects.All, e => e.Id == Effects.Canon(it.Props.Effect));
        bool transition = Effects.IsTransition(it.Props.Effect);
        Vis(transition, TransitionProps);
        Vis(!transition, EffectAmountRow, EffectAmountSlider);
        EffectAmountSlider.Value = it.Props.Amount * 100;
        EffectAmountValue.Text = $"{EffectAmountSlider.Value:0}%";
        PeakSlider.Value = it.Props.Peak * 100; HoldSlider.Value = it.Props.Hold * 100;
        PeakValue.Text = $"{PeakSlider.Value:0}% in"; HoldValue.Text = $"{HoldSlider.Value:0}%";
    }

    // ---- on the preview

    /// <summary>Shows the effects that are on at time <paramref name="t"/>: a dip is a black or white sheet over the picture, a shake moves and zooms the whole picture.</summary>
    private void ApplyEffects(Project p, double t)
    {
        double cover = 0; string coverColour = "#000000";
        double dx = 0, dy = 0, zoom = 1;
        foreach (var fx in p.Tracks.Where(tr => tr.Kind == TrackKind.Effect && !tr.Hidden).SelectMany(tr => tr.Items))
        {
            if (t < fx.Start || t >= fx.End) continue;
            double into = t - fx.Start, s = Effects.Strength(fx.Props, into, fx.Length);
            if (Effects.IsTransition(fx.Props.Effect)) { if (s > cover) { cover = s; coverColour = Effects.ColorOf(fx.Props); } }
            else
            {
                double a = fx.Props.Amount;
                dx -= Stage.ActualWidth * ShakeSide * a * s * Math.Sin(2 * Math.PI * 17 * into);
                dy -= Stage.ActualHeight * ShakeSide * a * s * Math.Cos(2 * Math.PI * 23 * into);
                zoom += ShakeZoom * a * s;
            }
        }
        try { EffectCover.Fill = (Brush)new BrushConverter().ConvertFromString(coverColour)!; } catch (FormatException) { EffectCover.Fill = Brushes.Black; }
        EffectCover.Opacity = Math.Clamp(cover, 0, 1);
        if (dx == 0 && dy == 0 && zoom == 1) StageContent.RenderTransform = Transform.Identity;
        else StageContent.RenderTransform = new TransformGroup { Children = { new ScaleTransform(zoom, zoom), new TranslateTransform(dx, dy) } };
    }
}
