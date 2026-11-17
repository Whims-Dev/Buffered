using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Buffered.UI;

public enum ItemKind { Video, Audio, Image, Text }
public enum TrackKind { Video, Audio, Text }

/// <summary>
/// What every kind of item shares, so a new kind of item (an image, a line of text, a sound file) is an addition and not a rewrite. Fades are in seconds.
/// Volume is a multiplier (1 = as recorded); Opacity is 0 to 1; Speed is 1 for normal.
/// </summary>
public sealed record ItemProps(double Opacity = 1, double Volume = 1, double FadeIn = 0, double FadeOut = 0, double Speed = 1, string? Text = null,
    double PosX = 0.5, double PosY = 0.5, double Scale = 1, double TextSize = 0.08, string Color = "#FFFFFF");

/// <summary>One piece on the timeline: a stretch of a source file (<see cref="SourceIn"/> to <see cref="SourceOut"/>, in the source's own seconds) placed at <see cref="Start"/>.</summary>
public sealed record TimelineItem(Guid Id, ItemKind Kind, string Source, double SourceIn, double SourceOut, double Start, ItemProps Props, Guid? LinkId = null, string? Label = null, int Stream = 0)
{
    /// <summary>How long it lasts on the timeline (a faster item is shorter).</summary>
    [JsonIgnore] public double Length => (SourceOut - SourceIn) / Math.Max(0.01, Props.Speed);
    [JsonIgnore] public double End => Start + Length;
}

public sealed record Track(Guid Id, TrackKind Kind, string Name, ImmutableList<TimelineItem> Items, bool Locked = false, bool Muted = false, bool Hidden = false);

/// <summary>
/// The whole edit as an unchanging value: every operation returns a new project, which is what makes undo and redo trivial (<see cref="EditHistory"/>) and the logic easy to test.
/// Times are in seconds on the timeline. Nothing here knows about the screen.
/// </summary>
public sealed record Project(ImmutableList<Track> Tracks)
{
    public const double MinLength = 0.05;   // nothing shorter than this is left behind by a cut

    [JsonIgnore] public double Duration => Tracks.SelectMany(t => t.Items).Select(i => i.End).DefaultIfEmpty(0).Max();

    [JsonIgnore] public IEnumerable<TimelineItem> Items => Tracks.SelectMany(t => t.Items);
    public TimelineItem? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);
    private int TrackOf(Guid itemId) => Tracks.FindIndex(t => t.Items.Any(i => i.Id == itemId));

    private Project Replace(int track, Func<ImmutableList<TimelineItem>, ImmutableList<TimelineItem>> change)
    {
        var t = Tracks[track];
        var items = change(t.Items).Sort((a, b) => a.Start.CompareTo(b.Start));
        return this with { Tracks = Tracks.SetItem(track, t with { Items = items }) };
    }

    public static Project Empty(params (TrackKind Kind, string Name)[] tracks) =>
        new(tracks.Select(t => new Track(Guid.NewGuid(), t.Kind, t.Name, ImmutableList<TimelineItem>.Empty)).ToImmutableList());

    /// <summary>Adds an empty lane. Picture lanes go above the others (the topmost lane is drawn on top); sound lanes go at the bottom.</summary>
    public Project AddTrack(TrackKind kind, string name)
    {
        var t = new Track(Guid.NewGuid(), kind, name, ImmutableList<TimelineItem>.Empty);
        if (kind != TrackKind.Audio) return this with { Tracks = Tracks.Insert(0, t) };
        return this with { Tracks = Tracks.Add(t) };
    }

    /// <summary>Removes a lane, but only an empty one.</summary>
    public Project RemoveTrack(int index) =>
        index >= 0 && index < Tracks.Count && Tracks[index].Items.IsEmpty ? this with { Tracks = Tracks.RemoveAt(index) } : this;

    public Project SetTrack(int index, Func<Track, Track> change) =>
        index >= 0 && index < Tracks.Count ? this with { Tracks = Tracks.SetItem(index, change(Tracks[index])) } : this;

    /// <summary>Puts an item on a track. Items on one track must not overlap, so a clash is refused (null).</summary>
    public Project? Add(int track, TimelineItem item)
    {
        if (track < 0 || track >= Tracks.Count || Tracks[track].Locked) return null;
        if (Tracks[track].Items.Any(o => Overlaps(o, item.Start, item.End))) return null;
        return Replace(track, l => l.Add(item));
    }

    private static bool Overlaps(TimelineItem o, double start, double end) => o.Start < end - 1e-9 && start < o.End - 1e-9;

    /// <summary>The razor: cuts one item in two at a timeline time. Only that item is cut (select the others too to cut them). Does nothing if the time is not well inside the item.
    /// The right half is not linked to anything; the left half keeps the link.</summary>
    public Project Split(Guid id, double time)
    {
        var item = Find(id);
        if (item is null) return this;
        var p = this;
        Guid? rightLink = null;
        foreach (var part in new[] { item })
        {
            if (time <= part.Start + MinLength || time >= part.End - MinLength) continue;
            int tr = p.TrackOf(part.Id);
            if (tr < 0 || p.Tracks[tr].Locked) continue;
            double cut = part.SourceIn + (time - part.Start) * part.Props.Speed;
            var left = part with { SourceOut = cut, Props = part.Props with { FadeOut = 0 } };
            var right = part with { Id = Guid.NewGuid(), SourceIn = cut, Start = time, Props = part.Props with { FadeIn = 0 }, LinkId = rightLink };
            p = p.Replace(tr, l => l.Remove(part).Add(left).Add(right));
        }
        return p;
    }

    private IEnumerable<TimelineItem> LinkedWith(TimelineItem item) =>
        item.LinkId is { } link ? Items.Where(i => i.LinkId == link) : new[] { item };

    /// <summary>Drags the left or right edge of an item to a timeline time (the source stays where it is; only how much of it is shown changes). Keeps the item inside its neighbours and its source.</summary>
    public Project Trim(Guid id, bool leftEdge, double time, double sourceLength = double.PositiveInfinity)
    {
        int tr = TrackOf(id);
        if (tr < 0 || Tracks[tr].Locked) return this;
        var item = Tracks[tr].Items.First(i => i.Id == id);
        var others = Tracks[tr].Items.Where(i => i.Id != id).ToList();
        double speed = item.Props.Speed;
        TimelineItem result;
        if (leftEdge)
        {
            double floor = others.Where(o => o.End <= item.Start + 1e-9).Select(o => o.End).DefaultIfEmpty(0).Max();
            floor = Math.Max(floor, item.Start - item.SourceIn / speed);                 // cannot reveal footage before the source begins
            double t = Math.Clamp(time, floor, item.End - MinLength);
            result = item with { Start = t, SourceIn = item.SourceIn + (t - item.Start) * speed };
        }
        else
        {
            double ceil = others.Where(o => o.Start >= item.End - 1e-9).Select(o => o.Start).DefaultIfEmpty(double.PositiveInfinity).Min();
            ceil = Math.Min(ceil, item.Start + (sourceLength - item.SourceIn) / speed);   // nor past its end
            double t = Math.Clamp(time, item.Start + MinLength, ceil);
            result = item with { SourceOut = item.SourceIn + (t - item.Start) * speed };
        }
        return Replace(tr, l => l.Remove(item).Add(result));
    }

    /// <summary>Moves an item to a new start (and perhaps another track of the same kind). If it would land on something it stops against the nearest free edge.</summary>
    public Project Move(Guid id, int toTrack, double start)
    {
        int from = TrackOf(id);
        if (from < 0 || toTrack < 0 || toTrack >= Tracks.Count) return this;
        if (Tracks[from].Locked || Tracks[toTrack].Locked || Tracks[from].Kind != Tracks[toTrack].Kind) return this;
        var item = Tracks[from].Items.First(i => i.Id == id);
        start = Math.Max(0, start);
        var others = Tracks[toTrack].Items.Where(o => o.Id != id).ToList();
        if (others.Any(o => Overlaps(o, start, start + item.Length)))
        {
            // it would land on something: it stops against the nearest edge that leaves room instead of refusing to move
            double len = item.Length;
            var candidates = others.SelectMany(o => new[] { o.End, o.Start - len }).Append(0).Where(c => c >= -1e-9)
                .Where(c => !others.Any(o => Overlaps(o, c, c + len))).ToList();
            if (candidates.Count == 0) return this;
            start = candidates.OrderBy(c => Math.Abs(c - start)).First();
        }
        var moved = item with { Start = Math.Max(0, start) };
        var p = Replace(from, l => l.Remove(item));
        return p.Replace(toTrack, l => l.Add(moved));
    }

    /// <summary>Moves several items by the same amount of time, each staying on its own track. If any would land on an item that is not moving (or on a locked track) nothing moves.</summary>
    public Project Shift(IReadOnlyCollection<Guid> ids, double delta)
    {
        var moving = Items.Where(i => ids.Contains(i.Id)).ToList();
        if (moving.Count == 0) return this;
        delta = Math.Max(delta, -moving.Min(i => i.Start));   // nothing goes before time zero
        if (Math.Abs(delta) < 1e-9) return this;
        var p = this;
        foreach (var tr in Tracks.Select((t, n) => (t, n)))
        {
            var mine = tr.t.Items.Where(i => ids.Contains(i.Id)).ToList();
            if (mine.Count == 0) continue;
            if (tr.t.Locked) return this;
            var still = tr.t.Items.Where(i => !ids.Contains(i.Id));
            if (mine.Any(m => still.Any(o => Overlaps(o, m.Start + delta, m.End + delta)))) return this;
            p = p.Replace(tr.n, l => l.Select(i => ids.Contains(i.Id) ? i with { Start = i.Start + delta } : i).ToImmutableList());
        }
        return p;
    }

    /// <summary>Deletes several items (each with its linked ones). With ripple, from the end backwards so earlier gaps close correctly.</summary>
    public Project DeleteMany(IEnumerable<Guid> ids, bool ripple)
    {
        var p = this;
        foreach (var item in ids.Select(Find).Where(i => i is not null).OrderByDescending(i => i!.Start).ToList())
            p = p.Delete(item!.Id, ripple);
        return p;
    }

    /// <summary>Deletes an item (and the ones linked to it). With <paramref name="ripple"/> the later items on those tracks slide left to close the gap.</summary>
    public Project Delete(Guid id, bool ripple)
    {
        var item = Find(id);
        if (item is null) return this;
        var p = this;
        foreach (var part in LinkedWith(item).ToList())
        {
            int tr = p.TrackOf(part.Id);
            if (tr < 0 || p.Tracks[tr].Locked) continue;
            double gap = part.Length, from = part.End;
            p = p.Replace(tr, l => l.Remove(part));
            if (ripple) p = p.Replace(tr, l => l.Select(o => o.Start >= from - 1e-9 ? o with { Start = o.Start - gap } : o).ToImmutableList());
        }
        return p;
    }

    /// <summary>Changes an item's properties (opacity, fades, volume...). Fades are kept inside the item's length.</summary>
    public Project SetProps(Guid id, Func<ItemProps, ItemProps> change)
    {
        int tr = TrackOf(id);
        if (tr < 0) return this;
        var item = Tracks[tr].Items.First(i => i.Id == id);
        var np = change(item.Props);
        np = np with
        {
            Opacity = Math.Clamp(np.Opacity, 0, 1),
            Volume = Math.Max(0, np.Volume),
            Speed = Math.Clamp(np.Speed, 0.1, 8),
            PosX = Math.Clamp(np.PosX, 0, 1), PosY = Math.Clamp(np.PosY, 0, 1),
            Scale = Math.Clamp(np.Scale, 0.05, 6), TextSize = Math.Clamp(np.TextSize, 0.02, 0.5),
            FadeIn = Math.Clamp(np.FadeIn, 0, item.Length),
            FadeOut = Math.Clamp(np.FadeOut, 0, item.Length),
        };
        return Replace(tr, l => l.Replace(item, item with { Props = np }));
    }

    /// <summary>
    /// A transition at a cut: everything that ends at that time fades out, and everything that starts there fades in, over <paramref name="seconds"/> (shortened to fit short items).
    /// Pictures dip through black and sound dips with them. Zero seconds takes the fades off. Items on locked lanes are left alone.
    /// </summary>
    public Project TransitionAt(double time, double seconds)
    {
        var p = this;
        foreach (var track in Tracks.Where(t => !t.Locked))
            foreach (var it in track.Items)
            {
                bool ends = Math.Abs(it.End - time) < 0.03, starts = Math.Abs(it.Start - time) < 0.03;
                if (!ends && !starts) continue;
                p = p.SetProps(it.Id, o => o with
                {
                    FadeOut = ends ? Math.Min(seconds, it.Length) : o.FadeOut,
                    FadeIn = starts ? Math.Min(seconds, it.Length) : o.FadeIn,
                });
            }
        return p;
    }

    /// <summary>Frees an item from the ones it was linked with (a picture and its sound), without moving anything.</summary>
    public Project Unlink(Guid id)
    {
        int tr = TrackOf(id);
        if (tr < 0) return this;
        var item = Tracks[tr].Items.First(i => i.Id == id);
        return Replace(tr, l => l.Replace(item, item with { LinkId = null }));
    }

    /// <summary>The nearest edge (start or end of any item, or time zero) within <paramref name="within"/> seconds of a time, for snapping while dragging; null if none. <paramref name="ignore"/> is the item being dragged.</summary>
    public double? SnapPoint(double time, double within, Guid? ignore = null) => SnapPoint(time, within, ignore is { } g ? new[] { g } : Array.Empty<Guid>());

    public double? SnapPoint(double time, double within, IReadOnlyCollection<Guid> ignore)
    {
        double? best = null;
        foreach (var t in Items.Where(i => !ignore.Contains(i.Id)).SelectMany(i => new[] { i.Start, i.End }).Append(0))
            if (Math.Abs(t - time) <= within && (best is null || Math.Abs(t - time) < Math.Abs(best.Value - time))) best = t;
        return best;
    }
}

/// <summary>An edit as it is saved: the project plus what is needed to open it again (its name, the picture's shape, how long each source file is).</summary>
public sealed record ProjectFile(int Version, string Name, double Aspect, Project Project, Dictionary<string, double> SourceLengths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a saved edit; null if the text is not one.</summary>
    public static ProjectFile? FromJson(string json)
    {
        try { var f = JsonSerializer.Deserialize<ProjectFile>(json, Options); return f is { Project: not null } ? f : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>The same edit with every source file path changed by <paramref name="map"/> (text items have none). Used to make an edit portable and to find its files again.</summary>
    public ProjectFile MapSources(Func<string, string> map) => this with
    {
        Project = Project with
        {
            Tracks = Project.Tracks.Select(t => t with
            {
                Items = t.Items.Select(i => i.Kind == ItemKind.Text || i.Source.Length == 0 ? i : i with { Source = map(i.Source) }).ToImmutableList(),
            }).ToImmutableList(),
        },
        SourceLengths = SourceLengths.GroupBy(kv => map(kv.Key), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Turns the file paths that are relative (as in a portable edit) into full paths, counting from the folder the edit file is in.</summary>
    public ProjectFile ResolveFrom(string folder) => MapSources(s => System.IO.Path.IsPathRooted(s) ? s : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, s)));

    /// <summary>The files an edit uses that no longer exist.</summary>
    public IEnumerable<string> MissingFiles() =>
        Project.Items.Where(i => i.Kind != ItemKind.Text && i.Source.Length > 0).Select(i => i.Source).Distinct(StringComparer.OrdinalIgnoreCase).Where(s => !System.IO.File.Exists(s));
}

/// <summary>Undo and redo over whole projects. A new edit clears whatever could have been redone.</summary>
public sealed class EditHistory
{
    private readonly Stack<Project> _undo = new(), _redo = new();
    private string? _lastMerge;
    public Project Current { get; private set; }
    public EditHistory(Project start) => Current = start;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Makes an edit the current state. An edit that changed nothing is not recorded. Edits with the same <paramref name="mergeKey"/> in a row share one undo step.</summary>
    public void Apply(Project next, string? mergeKey = null)
    {
        if (ReferenceEquals(next, Current)) return;
        if (mergeKey is not null && mergeKey == _lastMerge && _undo.Count > 0) { Current = next; return; }   // a slider being dragged is one undo step, not hundreds
        _undo.Push(Current); _redo.Clear(); Current = next; _lastMerge = mergeKey;
    }

    public void Undo() { _lastMerge = null; if (_undo.Count > 0) { _redo.Push(Current); Current = _undo.Pop(); } }
    public void Redo() { _lastMerge = null; if (_redo.Count > 0) { _undo.Push(Current); Current = _redo.Pop(); } }
}
