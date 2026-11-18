using System.Windows;
using System.Windows.Controls;

namespace Buffered.UI;

/// <summary>The shape of the video, snapping, and copying, pasting and duplicating items.</summary>
public partial class AdvancedEditor
{
    private static readonly (string Label, double Aspect)[] Shapes =
    {
        ("Auto", 0), ("16:9 wide", 16.0 / 9), ("9:16 tall (phone)", 9.0 / 16), ("1:1 square", 1), ("4:5 portrait", 4.0 / 5), ("4:3", 4.0 / 3), ("21:9 cinema", 21.0 / 9),
    };
    private bool _aspectBoxBusy;

    private void InitMore()
    {
        _aspectBoxBusy = true;
        foreach (var s in Shapes) AspectBox.Items.Add(s.Label);
        AspectBox.SelectedIndex = 0;
        _aspectBoxBusy = false;
        StatusText.Text = Hint;
    }

    /// <summary>Shows which shape the video has (after an edit was opened or the first clip decided it).</summary>
    private void ShowAspect()
    {
        _aspectBoxBusy = true;
        int i = Array.FindIndex(Shapes, s => s.Aspect > 0 && Math.Abs(s.Aspect - _aspect) < 0.01);
        // "Auto" says what the shape is at the moment, for example "Auto (16:9)"
        string ratio = i > 0 ? Shapes[i].Label.Split(' ')[0] : $"{_aspect:0.##}:1";
        AspectBox.Items[0] = $"Auto ({ratio})";
        AspectBox.SelectedIndex = _aspectSet && i > 0 ? i : 0;
        _aspectBoxBusy = false;
    }

    private void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_aspectBoxBusy || AspectBox.SelectedIndex < 0 || !_hasSession) return;
        var (_, aspect) = Shapes[AspectBox.SelectedIndex];
        if (aspect <= 0)
        {
            // "the first clip's": the shape of the first video put on the timeline, or wide until there is one
            _aspectSet = false;
            _aspect = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : 16.0 / 9;
        }
        else { _aspect = aspect; _aspectSet = true; }
        FitStage();
        _savedProject = Project.Empty();   // the shape is saved with the edit, so changing it counts as an unsaved change
        Focus();
    }

    /// <summary>With Magnetic on, the main picture lane is kept free of gaps.</summary>
    private Project Magnetic(Project p) => RippleToggle.IsChecked == true ? p.CloseGaps() : p;

    /// <summary>Q cuts the start of the selected items off at the playhead, W the end (like dragging the edge there).</summary>
    private void TrimToPlayhead(bool start)
    {
        var p = _h.Current; int n = 0;
        foreach (var id in Tl.Selection.ToList())
        {
            if (p.Find(id) is not { } it || _t <= it.Start + 0.05 || _t >= it.End - 0.05) continue;
            double limit = TimelineView.SourceLengths.TryGetValue(it.Source, out var l) ? l : double.PositiveInfinity;
            p = p.Trim(id, start, _t, limit); n++;
        }
        if (n == 0) { StatusText.Text = "Select an item and put the playhead inside it first"; return; }
        _h.Apply(Magnetic(p)); AfterEdit();
    }

    /// <summary>Up and Down move the playhead to the previous or next place where something starts or ends.</summary>
    private void JumpToCut(bool forward)
    {
        var edges = _h.Current.Items.SelectMany(i => new[] { i.Start, i.End }).Concat(_h.Current.Marks).Append(0).Distinct().OrderBy(t => t).ToList();
        double? target = forward ? edges.Where(t => t > _t + 0.02).Cast<double?>().FirstOrDefault() : edges.Where(t => t < _t - 0.02).Cast<double?>().LastOrDefault();
        if (target is { } to) { SetTime(to, true); Tl.KeepVisible(to); }
    }

    private void Snap_Click(object sender, RoutedEventArgs e) { Tl.SnapEnabled = SnapToggle.IsChecked == true; Focus(); }

    // ---- copy, paste, duplicate

    private List<(int Track, TimelineItem Item)> _clipboard = new();

    private void CopySelection()
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select something to copy first"; return; }
        _clipboard = _h.Current.Pick(Tl.Selection);
        StatusText.Text = $"Copied {_clipboard.Count} item{(_clipboard.Count == 1 ? "" : "s")}: Ctrl+V pastes at the playhead";
    }

    /// <summary>Ctrl+X: copies the selected items and takes them off the timeline (Ctrl+V puts them back, anywhere).</summary>
    private void CutSelection()
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select something to cut first"; return; }
        var picked = _h.Current.Pick(Tl.Selection);
        DeleteSelected(false);
        if (_h.Current.Items.Any(i => picked.Any(x => x.Item.Id == i.Id))) { StatusText.Text = "That is on a locked lane, so it can't be cut"; return; }   // nothing was removed: keep the old clipboard
        _clipboard = picked;
        StatusText.Text = $"Cut {picked.Count} item{(picked.Count == 1 ? "" : "s")}: Ctrl+V pastes at the playhead";
    }

    private void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0) { StatusText.Text = "Nothing copied yet"; return; }
        PasteCopies(_clipboard, _t);
    }

    private void Duplicate()
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select something to duplicate first"; return; }
        var picked = _h.Current.Pick(Tl.Selection);
        PasteCopies(picked, picked.Max(x => x.Item.End));   // right after the last of them
    }

    private void PasteCopies(List<(int Track, TimelineItem Item)> items, double at)
    {
        var (next, ids) = _h.Current.Paste(items, at);
        if (ids.Count == 0) { StatusText.Text = "There was no room to paste"; return; }
        _h.Apply(next);
        Tl.SetSelection(ids); _selected = ids[^1];
        AfterEdit();
    }
}
