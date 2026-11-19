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
    }

    /// <summary>Puts an effect on an effect lane (the first with room, or a new one) and selects it.</summary>
    private void AddEffectItem(string type, double start, double length)
    {
        start = Math.Max(0, start);
        var item = new TimelineItem(Guid.NewGuid(), ItemKind.Effect, "", 0, length, start, new ItemProps(Effect: type), null, Effects.NameOf(type));
        _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Effect, item, true, "Effects"));
        AfterEdit();
        Tl.SetSelection(new[] { item.Id }); _selected = item.Id; ShowProps(); RenderOverlay();
        StatusText.Text = Effects.IsTransition(type) ? "Transition added over the cut: drag it or pull its edges to move it or change its length" : "Shake added at the playhead: drag it or pull its edges to move it or change its length";
    }

    /// <summary>A transition centred on a cut (the selected one, or the nearest to the playhead, or the playhead itself).</summary>
    private void AddTransition(string type, double seconds)
    {
        double at = CutTime() ?? _t;
        AddEffectItem(type, Math.Max(0, at - seconds / 2), seconds);
    }

    private void EffectAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string type }) return;
        if (Effects.IsTransition(type)) AddTransition(type, 1.0); else AddEffectItem(type, _t, 0.5);
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

    /// <summary>Fills in the effect part of the properties panel.</summary>
    private void ShowEffectProps(TimelineItem it)
    {
        int i = Array.FindIndex(Effects.All, e => e.Id == it.Props.Effect);
        EffectBox.SelectedIndex = i;
        EffectAmountSlider.Value = it.Props.Amount * 100;
        EffectAmountValue.Text = $"{EffectAmountSlider.Value:0}%";
        Vis(!Effects.IsTransition(it.Props.Effect), EffectAmountRow, EffectAmountSlider);
    }

    // ---- on the preview

    /// <summary>Shows the effects that are on at time <paramref name="t"/>: a dip is a black or white sheet over the picture, a shake moves and zooms the whole picture.</summary>
    private void ApplyEffects(Project p, double t)
    {
        double cover = 0; bool white = false;
        double dx = 0, dy = 0, zoom = 1;
        foreach (var fx in p.Tracks.Where(tr => tr.Kind == TrackKind.Effect && !tr.Hidden).SelectMany(tr => tr.Items))
        {
            if (t < fx.Start || t >= fx.End) continue;
            double into = t - fx.Start, s = Effects.Strength(fx.Props.Effect, into, fx.Length);
            if (Effects.IsTransition(fx.Props.Effect)) { if (s > cover) { cover = s; white = fx.Props.Effect == Effects.DipWhite; } }
            else
            {
                double a = fx.Props.Amount;
                dx -= Stage.ActualWidth * ShakeSide * a * s * Math.Sin(2 * Math.PI * 17 * into);
                dy -= Stage.ActualHeight * ShakeSide * a * s * Math.Cos(2 * Math.PI * 23 * into);
                zoom += ShakeZoom * a * s;
            }
        }
        EffectCover.Fill = white ? Brushes.White : Brushes.Black;
        EffectCover.Opacity = Math.Clamp(cover, 0, 1);
        if (dx == 0 && dy == 0 && zoom == 1) StageContent.RenderTransform = Transform.Identity;
        else StageContent.RenderTransform = new TransformGroup { Children = { new ScaleTransform(zoom, zoom), new TranslateTransform(dx, dy) } };
    }
}
