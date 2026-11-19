using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Buffered.UI;

public enum ItemKind { Video, Audio, Image, Text, Effect }
public enum TrackKind { Video, Audio, Text, Effect }

/// <summary>
/// What every kind of item shares, so a new kind of item (an image, a line of text, a sound file) is an addition and not a rewrite. Fades are in seconds.
/// Volume is a multiplier (1 = as recorded); Opacity is 0 to 1; Speed is 1 for normal.
/// </summary>
public sealed record ItemProps(double Opacity = 1, double Volume = 1, double FadeIn = 0, double FadeOut = 0, double Speed = 1, string? Text = null,
    double PosX = 0.5, double PosY = 0.5, double Scale = 1, double TextSize = 0.08, string Color = "#FFFFFF", ImmutableList<KeyTrack>? Keys = null, double Rotation = 0, double CropL = 0, double CropT = 0, double CropR = 0, double CropB = 0,
    string Font = "Segoe UI", bool Bold = true, bool Italic = false, double Outline = 0, string OutlineColor = "#000000", string? Box = null, double GifSpeed = 1, string? Effect = null, double Amount = 1, double Peak = 0.5, double Hold = 0)
{
    /// <summary>The value a property has when it is not animated (the slider's value).</summary>
    public double Base(KeyProp p) => p switch { KeyProp.Opacity => Opacity, KeyProp.Volume => Volume, KeyProp.PosX => PosX, KeyProp.PosY => PosY, KeyProp.Speed => Speed, KeyProp.TextSize => TextSize, KeyProp.Rotation => Rotation, _ => Scale };

    /// <summary>True when part of the picture is cut off (each edge is a share of the picture's width or height).</summary>
    public bool HasCrop => CropL > 0.001 || CropT > 0.001 || CropR > 0.001 || CropB > 0.001;

    public bool HasKeys(KeyProp p)
    {
        if (Keys is null || p == KeyProp.Speed) return false;   // speed is no longer animated (see KeyProp.Speed)
        foreach (var k in Keys) if (k.Prop == p && k.Frames.Count > 0) return true;   // (a plain loop: this is asked many times a second while playing)
        return false;
    }

    public ImmutableList<Keyframe> FramesOf(KeyProp p)
    {
        if (Keys is not null && p != KeyProp.Speed) foreach (var k in Keys) if (k.Prop == p) return k.Frames;
        return ImmutableList<Keyframe>.Empty;
    }

    /// <summary>
    /// The value of a property <paramref name="t"/> seconds into the item: the slider's value if it is not animated, otherwise a straight line between the two keyframes around that moment
    /// (before the first keyframe it has the first one's value, after the last the last one's).
    /// </summary>
    public double ValueAt(KeyProp p, double t)
    {
        var f = FramesOf(p);
        if (f.Count == 0) return Base(p);
        if (t <= f[0].Time) return f[0].Value;
        if (t >= f[^1].Time) return f[^1].Value;
        int i = 1; while (f[i].Time < t) i++;
        double span = f[i].Time - f[i - 1].Time;
        return span <= 1e-9 ? f[i].Value : f[i - 1].Value + (f[i].Value - f[i - 1].Value) * (t - f[i - 1].Time) / span;
    }

    public static double Limit(KeyProp p, double v) => p switch
    {
        KeyProp.Opacity => Math.Clamp(v, 0, 1), KeyProp.Volume => Math.Clamp(v, 0, 2), KeyProp.PosX or KeyProp.PosY => Math.Clamp(v, 0, 1),
        KeyProp.Speed => Math.Clamp(v, 0.1, 8), KeyProp.TextSize => Math.Clamp(v, 0.02, 0.5), KeyProp.Rotation => Math.Clamp(v, -360, 360), _ => Math.Clamp(v, 0.05, 6),
    };

    private ItemProps WithFrames(KeyProp p, ImmutableList<Keyframe> frames)
    {
        var others = (Keys ?? ImmutableList<KeyTrack>.Empty).Where(k => k.Prop != p);
        var all = frames.Count == 0 ? others : others.Append(new KeyTrack(p, frames));
        var list = all.OrderBy(k => k.Prop).ToImmutableList();
        return this with { Keys = list.IsEmpty ? null : list };
    }

    /// <summary>Puts a keyframe in (or changes the one that is already within a few hundredths of a second of that time).</summary>
    public ItemProps WithKey(KeyProp p, double time, double value)
    {
        if (p == KeyProp.Speed) return this;
        var f = FramesOf(p);
        int near = f.FindIndex(k => Math.Abs(k.Time - time) < 0.03);
        var key = new Keyframe(near >= 0 ? f[near].Time : time, Limit(p, value));
        return WithFrames(p, (near >= 0 ? f.SetItem(near, key) : f.Add(key)).Sort((a, b) => a.Time.CompareTo(b.Time)));
    }

    /// <summary>Replaces whatever keyframes a property has between two times (item time) with these ones.</summary>
    public ItemProps WithKeyBurst(KeyProp p, double from, double to, IEnumerable<(double Time, double Value)> frames)
    {
        var r = this;
        foreach (var k in FramesOf(p).Where(k => k.Time > from - 1e-6 && k.Time < to + 1e-6).ToList()) r = r.WithoutKey(p, k.Time);
        foreach (var (t, v) in frames) r = r.WithKey(p, t, v);
        return r;
    }

    public ItemProps WithoutKey(KeyProp p, double time)
    {
        var f = FramesOf(p);
        int near = f.FindIndex(k => Math.Abs(k.Time - time) < 0.03);
        return near < 0 ? this : WithFrames(p, f.RemoveAt(near));
    }

    public ItemProps ClearKeys(KeyProp p) => WithFrames(p, ImmutableList<Keyframe>.Empty);

    /// <summary>How many seconds of the source have been played <paramref name="t"/> seconds into the item.</summary>
    public double SourceAt(double t) => Speed * t;

    /// <summary>The opposite of <see cref="SourceAt"/>: how many seconds into the item <paramref name="source"/> seconds of the source have been played.</summary>
    public double OutAt(double source) => source / Math.Max(0.01, Speed);

    /// <summary>The keyframes of the part of an item that starts <paramref name="from"/> seconds in and is <paramref name="length"/> seconds long, with times counted from its new start.</summary>
    public ItemProps Slice(double from, double length)
    {
        if (Keys is null) return this;
        var result = this;
        foreach (var k in Keys)
        {
            var kept = k.Frames.Where(f => f.Time > from + 0.001 && f.Time < from + length - 0.001).Select(f => f with { Time = f.Time - from }).ToList();
            kept.Insert(0, new Keyframe(0, ValueAt(k.Prop, from)));                         // the animation's value at the new start, so the part looks the same as it did
            kept.Add(new Keyframe(length, ValueAt(k.Prop, from + length)));                  // and at the new end
            result = result.WithFrames(k.Prop, kept.ToImmutableList());
        }
        return result;
    }

    /// <summary>The same keyframes with every time moved by <paramref name="delta"/> seconds.</summary>
    public ItemProps ShiftKeys(double delta) =>
        Keys is null ? this : this with { Keys = Keys.Select(k => k with { Frames = k.Frames.Select(f => f with { Time = f.Time + delta }).ToImmutableList() }).ToImmutableList() };
}

/// <summary>A property that can change over the time an item is on the timeline.</summary>
public enum KeyProp
{
    Opacity, Volume, PosX, PosY, Scale,
    /// <summary>Not animated any more (the time stretching made keyframes drift); kept only so edits saved while it existed still open. Its keyframes are dropped on opening.</summary>
    Speed,
    TextSize,
    /// <summary>Turn, in degrees (clockwise).</summary>
    Rotation,
}

/// <summary>A value a property has at a moment, counted in seconds from the start of the item.</summary>
public sealed record Keyframe(double Time, double Value);

public sealed record KeyTrack(KeyProp Prop, ImmutableList<Keyframe> Frames);

/// <summary>One piece on the timeline: a stretch of a source file (<see cref="SourceIn"/> to <see cref="SourceOut"/>, in the source's own seconds) placed at <see cref="Start"/>.</summary>
public sealed record TimelineItem(Guid Id, ItemKind Kind, string Source, double SourceIn, double SourceOut, double Start, ItemProps Props, Guid? LinkId = null, string? Label = null, int Stream = 0)
{
    /// <summary>How long it lasts on the timeline (a faster item is shorter).</summary>
    [JsonIgnore] public double Length => Props.OutAt(SourceOut - SourceIn);
    [JsonIgnore] public double End => Start + Length;

    /// <summary>
    /// The fade in and fade out as they really play: never longer than the item, and if the two together are longer than it they are shortened in the same proportion,
    /// so the fade out always ends with the item (an item trimmed shorter after its fades were set does not end half faded).
    /// </summary>
    [JsonIgnore] public (double In, double Out) Fades
    {
        get
        {
            double len = Length, fin = Math.Max(0, Props.FadeIn), fout = Math.Max(0, Props.FadeOut);
            if (fin + fout > len && fin + fout > 0) { double k = len / (fin + fout); fin *= k; fout *= k; }
            return (fin, fout);
        }
    }
}

/// <param name="HeightScale">How tall the lane is drawn compared with the normal height (1 is normal); the user drags it.</param>
/// <summary>
/// The kinds of effect an effect-lane item can be (<see cref="ItemProps.Effect"/>). A transition is one kind whose properties say what it does: the
/// colour it goes to, where in its length it is strongest (<see cref="ItemProps.Peak"/>) and how long it stays at full strength (<see cref="ItemProps.Hold"/>).
/// Dipping through black, through white, or a quick flash are all just different values of those.
/// </summary>
public static class Effects
{
    public const string Transition = "transition", Shake = "shake";
    public const string LegacyDipBlack = "dip-black", LegacyDipWhite = "dip-white";   // what an early build saved; read as a transition with that colour
    public static readonly (string Id, string Name)[] All = { (Transition, "Transition"), (Shake, "Shake") };

    public static string Canon(string? id) => id is LegacyDipBlack or LegacyDipWhite ? Transition : id ?? "";
    public static bool IsTransition(string? id) => Canon(id) == Transition;
    public static string NameOf(string? id) => All.FirstOrDefault(e => e.Id == Canon(id)).Name ?? "Effect";

    /// <summary>The colour a transition goes to, as #RRGGBB.</summary>
    public static string ColorOf(ItemProps p) => p.Effect == LegacyDipWhite ? "#FFFFFF" : p.Effect == LegacyDipBlack ? "#000000" : p.Color;

    /// <summary>
    /// How strongly the effect shows at a moment <paramref name="t"/> seconds into an item <paramref name="length"/> seconds long, from 0 to 1.
    /// A transition rises to full strength (reaching it at the peak), stays there for the hold, and falls away again; a shake starts at full strength and dies away.
    /// </summary>
    public static double Strength(ItemProps p, double t, double length)
    {
        if (length <= 0 || t < 0 || t > length) return 0;
        double u = t / length;
        if (!IsTransition(p.Effect)) return 1 - u;
        var (attack, hold, release) = Shape(p);
        if (u < attack) return attack <= 0 ? 1 : u / attack;
        if (u <= attack + hold) return 1;
        return release <= 0 ? 0 : Math.Max(0, 1 - (u - attack - hold) / release);
    }

    /// <summary>The three parts of a transition as shares of its length: rising, holding at full strength, and falling. They add up to 1.</summary>
    public static (double Attack, double Hold, double Release) Shape(ItemProps p)
    {
        double hold = Math.Clamp(p.Hold, 0, 0.95), peak = Math.Clamp(p.Peak, 0, 1);
        double attack = peak * (1 - hold);
        return (attack, hold, 1 - hold - attack);
    }
}

public sealed record Track(Guid Id, TrackKind Kind, string Name, ImmutableList<TimelineItem> Items, bool Locked = false, bool Muted = false, bool Hidden = false, double HeightScale = 1, bool Solo = false)
{
    /// <summary>Whether this sound lane is heard: not muted, and if any lane is soloed, only soloed ones.</summary>
    public bool IsHeardIn(Project p) => Kind == TrackKind.Audio && !Muted && (Solo || !p.Tracks.Any(t => t.Solo));
}

/// <summary>
/// The whole edit as an unchanging value: every operation returns a new project, which is what makes undo and redo trivial (<see cref="EditHistory"/>) and the logic easy to test.
/// Times are in seconds on the timeline. Nothing here knows about the screen.
/// </summary>
public sealed record Project(ImmutableList<Track> Tracks, ImmutableList<double>? Markers = null)
{
    /// <summary>Times the user marked on the ruler (to come back to, and to snap to).</summary>
    [JsonIgnore] public ImmutableList<double> Marks => Markers ?? ImmutableList<double>.Empty;

    /// <summary>Puts a marker at a time, or takes the one that is already there (within a twentieth of a second) away.</summary>
    public Project ToggleMarker(double time)
    {
        int near = Marks.FindIndex(m => Math.Abs(m - time) < 0.05);
        return this with { Markers = near >= 0 ? Marks.RemoveAt(near) : Marks.Add(time).Sort() };
    }

    /// <summary>Puts markers at these times (those already within a few hundredths of one are skipped).</summary>
    public Project AddMarkers(IEnumerable<double> times)
    {
        var list = Marks;
        foreach (var t in times.Where(t => t >= 0)) if (!list.Any(m => Math.Abs(m - t) < 0.05)) list = list.Add(t);
        return this with { Markers = list.Sort() };
    }

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
    public Project Split(Guid id, double time, Guid? rightLink = null)
    {
        var item = Find(id);
        if (item is null) return this;
        var p = this;
        foreach (var part in new[] { item })
        {
            if (time <= part.Start + MinLength || time >= part.End - MinLength) continue;
            int tr = p.TrackOf(part.Id);
            if (tr < 0 || p.Tracks[tr].Locked) continue;
            double cut = part.SourceIn + part.Props.SourceAt(time - part.Start);
            double leftLen = time - part.Start, rightLen = part.End - time;
            var left = part with { SourceOut = cut, Props = part.Props.Slice(0, leftLen) with { FadeOut = 0 } };
            var right = part with { Id = Guid.NewGuid(), SourceIn = cut, Start = time, Props = part.Props.Slice(leftLen, rightLen) with { FadeIn = 0 }, LinkId = rightLink };
            p = p.Replace(tr, l => l.Remove(part).Add(left).Add(right));
        }
        return p;
    }

    /// <summary>
    /// Cuts several items at a time. Items that were linked (a picture and its sound) are cut as one: their left halves stay linked to each other, and their right halves are linked
    /// to each other too (as a new group), so the two pieces each behave as a clip with its sound.
    /// </summary>
    public Project SplitMany(IEnumerable<Guid> ids, double time)
    {
        var p = this;
        var newLinks = new Dictionary<Guid, Guid>();
        foreach (var id in ids.ToList())
        {
            var item = p.Find(id);
            if (item is null) continue;
            Guid? rightLink = null;
            if (item.LinkId is { } link) { if (!newLinks.TryGetValue(link, out var nl)) newLinks[link] = nl = Guid.NewGuid(); rightLink = nl; }
            p = p.Split(id, time, rightLink);
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
        double speed = Math.Max(0.1, item.Props.ValueAt(KeyProp.Speed, 0));
        TimelineItem result;
        if (leftEdge)
        {
            double floor = others.Where(o => o.End <= item.Start + 1e-9).Select(o => o.End).DefaultIfEmpty(0).Max();
            floor = Math.Max(floor, item.Start - item.SourceIn / speed);                 // cannot reveal footage before the source begins
            double t = Math.Clamp(time, floor, item.End - MinLength);
            result = item with { Start = t, SourceIn = item.SourceIn + item.Props.SourceAt(t - item.Start), Props = item.Props.ShiftKeys(-(t - item.Start)) };   // the animation stays with the picture
        }
        else
        {
            double ceil = others.Where(o => o.Start >= item.End - 1e-9).Select(o => o.Start).DefaultIfEmpty(double.PositiveInfinity).Min();
            ceil = Math.Min(ceil, item.Start + item.Props.OutAt(sourceLength - item.SourceIn));   // nor past its end
            double t = Math.Clamp(time, item.Start + MinLength, ceil);
            result = item with { SourceOut = item.SourceIn + item.Props.SourceAt(t - item.Start) };
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
            Scale = Math.Clamp(np.Scale, 0.05, 6), TextSize = Math.Clamp(np.TextSize, 0.02, 0.5), Rotation = Math.Clamp(np.Rotation, -360, 360),
            CropL = Math.Clamp(np.CropL, 0, 0.9), CropT = Math.Clamp(np.CropT, 0, 0.9), CropR = Math.Clamp(np.CropR, 0, 0.9), CropB = Math.Clamp(np.CropB, 0, 0.9),
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

    /// <summary>
    /// A real cross-dissolve at a cut. What starts at the cut is brought forward by <paramref name="seconds"/> (using the footage just before where it starts, so the clip must have
    /// that much before its start: a clip cut from a longer one does) and placed on a lane of its own above, fading in while the picture before it carries on underneath. Its sound is
    /// brought forward too and fades in while the sound before it fades out. Returns how many items were changed (0 when there was no footage to use or nothing meets at that time).
    /// </summary>
    public (Project Project, int Changed) CrossDissolveAt(double time, double seconds)
    {
        var p = this; int changed = 0;
        foreach (var next in Items.Where(i => Math.Abs(i.Start - time) < 0.03 && i.Kind is ItemKind.Video or ItemKind.Image or ItemKind.Audio).ToList())
        {
            int lane = p.TrackOf(next.Id);
            if (lane < 0 || p.Tracks[lane].Locked) continue;
            var before = p.Tracks[lane].Items.FirstOrDefault(i => i.Id != next.Id && Math.Abs(i.End - time) < 0.03);
            if (before is null) continue;
            double speed = Math.Max(0.1, next.Props.Speed);
            double d = Math.Min(seconds, Math.Min(next.SourceIn / speed, before.Length));        // how much extra footage there is before it starts
            if (next.Kind == ItemKind.Image) d = Math.Min(seconds, before.Length);                 // a picture has no footage to run out of
            if (d < 0.05) continue;

            var moved = next with
            {
                Start = time - d,
                SourceIn = next.Kind == ItemKind.Image ? next.SourceIn : next.SourceIn - d * speed,
                SourceOut = next.Kind == ItemKind.Image ? next.SourceOut + d * speed : next.SourceOut,
                Props = next.Props.ShiftKeys(d) with { FadeIn = d },
            };
            // it overlaps what is before it on its own lane, so it goes on another lane of the same kind
            var kind = p.Tracks[lane].Kind;
            var without = p.Replace(lane, l => l.Remove(next));
            Project? placed = null;
            var order = kind == TrackKind.Audio ? Enumerable.Range(0, without.Tracks.Count).Where(i => i != lane && without.Tracks[i].Kind == kind).ToList()
                                                : Enumerable.Range(0, lane).Reverse().Where(i => without.Tracks[i].Kind == kind).ToList();   // picture: the nearest lane above
            foreach (var i in order) { placed = without.Add(i, moved); if (placed is not null) break; }
            if (placed is null)
            {
                var grown = without.AddTrack(kind, kind == TrackKind.Audio ? "Sound" : "Overlay");
                placed = grown.Add(kind == TrackKind.Audio ? grown.Tracks.Count - 1 : 0, moved);
            }
            if (placed is null) continue;
            p = placed; changed++;
            if (before.Kind == ItemKind.Audio) p = p.SetProps(before.Id, o => o with { FadeOut = d });   // the sound before fades out while the new sound fades in
        }
        return (p, changed);
    }

    /// <summary>
    /// The main picture lane (the lowest picture lane) without gaps: each item moves back against the one before it, the first to time zero. The sound linked to a moved
    /// picture moves with it (when it can). Returns the project unchanged when there is nothing to close.
    /// </summary>
    public Project CloseGaps()
    {
        int lane = Tracks.FindLastIndex(t => t.Kind == TrackKind.Video && !t.Locked);
        if (lane < 0) return this;
        var p = this; double end = 0;
        foreach (var id in Tracks[lane].Items.OrderBy(i => i.Start).Select(i => i.Id).ToList())
        {
            var item = p.Find(id)!;
            double delta = end - item.Start;
            if (delta < -1e-6)   // a gap before it: it moves back
            {
                var group = item.LinkId is { } link ? p.Items.Where(i => i.LinkId == link).Select(i => i.Id).ToList() : new List<Guid> { id };
                var moved = p.Shift(group, delta);
                if (ReferenceEquals(moved, p)) moved = p.Shift(new List<Guid> { id }, delta);     // the sound would not fit: the picture still closes the gap
                p = moved;
                item = p.Find(id)!;
            }
            end = item.End;
        }
        return p;
    }

    /// <summary>The track number each of these items is on, with the item (for copying).</summary>
    public List<(int Track, TimelineItem Item)> Pick(IEnumerable<Guid> ids) =>
        ids.Select(id => (Track: TrackOf(id), Item: Find(id))).Where(x => x.Track >= 0 && x.Item is not null).Select(x => (Track: x.Track, Item: x.Item!)).OrderBy(x => x.Item.Start).ToList();

    /// <summary>
    /// Puts copies of items in, the first one starting at <paramref name="at"/> and the others the same distance after it as before. Each goes on its own lane if it fits there,
    /// otherwise on another lane of the same kind, otherwise on a new lane. The copies have new ids and are not linked to anything. Returns the new project and the ids of the copies.
    /// </summary>
    public (Project Project, List<Guid> NewIds) Paste(IReadOnlyList<(int Track, TimelineItem Item)> items, double at)
    {
        var p = this; var ids = new List<Guid>();
        if (items.Count == 0) return (p, ids);
        double first = items.Min(x => x.Item.Start);
        var links = new Dictionary<Guid, Guid>();
        foreach (var (track, item) in items)
        {
            Guid? link = item.LinkId is { } old ? (links.TryGetValue(old, out var made) ? made : links[old] = Guid.NewGuid()) : null;
            var copy = item with { Id = Guid.NewGuid(), Start = Math.Max(0, at + (item.Start - first)), LinkId = link };
            var kind = item.Kind switch { ItemKind.Audio => TrackKind.Audio, ItemKind.Text => TrackKind.Text, ItemKind.Effect => TrackKind.Effect, _ => TrackKind.Video };
            var order = new List<int>();
            if (track >= 0 && track < p.Tracks.Count && p.Tracks[track].Kind == kind) order.Add(track);
            order.AddRange(Enumerable.Range(0, p.Tracks.Count).Where(i => p.Tracks[i].Kind == kind));
            Project? placed = null;
            foreach (var i in order.Distinct()) { placed = p.Add(i, copy); if (placed is not null) break; }
            if (placed is null)
            {
                var grown = p.AddTrack(kind, kind == TrackKind.Audio ? "Sound" : kind == TrackKind.Text ? "Text" : kind == TrackKind.Effect ? "Effects" : "Video");
                placed = grown.Add(kind == TrackKind.Audio ? grown.Tracks.Count - 1 : 0, copy);
            }
            if (placed is null) continue;
            p = placed; ids.Add(copy.Id);
        }
        return (p, ids);
    }

    /// <summary>Sets a keyframe of a property at a timeline time (it must be inside the item), so the property starts to change over time.</summary>
    public Project SetKey(Guid id, KeyProp prop, double timelineTime, double value)
    {
        var item = Find(id);
        if (item is null) return this;
        double t = Math.Clamp(timelineTime - item.Start, 0, item.Length);
        return SetProps(id, o => o.WithKey(prop, t, value));
    }

    /// <summary>
    /// A speed ramp: from the playhead on, the next few moments of the item (and of the sound linked to it) play at different speeds, one after another (each is source seconds and a speed),
    /// then the rest carries on at its old speed. The item is cut into pieces for this, and what comes after it on each lane moves to make room (or close up).
    /// </summary>
    public Project SpeedRamp(Guid id, double timelineTime, IReadOnlyList<(double Source, double Speed)> segments)
    {
        var anchor = Find(id);
        if (anchor is null) return this;
        var p = this;
        var made = new Dictionary<int, Guid>();
        bool any = false;
        foreach (var m in LinkedWith(anchor).ToList())
        {
            int tr = p.TrackOf(m.Id);
            if (tr < 0 || p.Tracks[tr].Locked) return this;
            double r = timelineTime - m.Start;
            if (r <= MinLength || r >= m.Length - MinLength) continue;
            double sp = m.Props.Speed, s0 = m.SourceIn + sp * r, cursor = s0, at = timelineTime;
            var pieces = new List<TimelineItem>();
            Guid? Link(int k) { if (m.LinkId is null) return null; if (!made.TryGetValue(k, out var g)) made[k] = g = Guid.NewGuid(); return g; }
            var pre = m with { SourceOut = s0, Props = m.Props.Slice(0, r) with { FadeOut = 0 } };
            for (int k = 0; k < segments.Count; k++)
            {
                double len = Math.Min(segments[k].Source, m.SourceOut - cursor);
                if (len < 0.02) break;
                double speed = Math.Clamp(segments[k].Speed, 0.1, 8);
                pieces.Add(m with { Id = Guid.NewGuid(), SourceIn = cursor, SourceOut = cursor + len, Start = at, LinkId = Link(k),
                                    Props = m.Props.Slice((cursor - m.SourceIn) / sp, len / sp) with { Speed = speed, FadeIn = 0, FadeOut = 0 } });
                at += len / speed; cursor += len;
            }
            if (m.SourceOut - cursor > 0.02)
            {
                var rest = m.Props.Slice((cursor - m.SourceIn) / sp, (m.SourceOut - cursor) / sp) with { FadeIn = 0 };
                pieces.Add(m with { Id = Guid.NewGuid(), SourceIn = cursor, Start = at, LinkId = Link(99), Props = rest });
                at += (m.SourceOut - cursor) / sp;
            }
            if (pieces.Count == 0) continue;
            double delta = at - m.End;
            p = p.Replace(tr, l => l.Select(i => i.Id != m.Id && i.Start >= m.End - 1e-9 ? i with { Start = Math.Max(0, i.Start + delta) } : i).Where(i => i.Id != m.Id).Append(pre).Concat(pieces).ToImmutableList());
            any = true;
        }
        return any ? p : this;
    }

    /// <summary>A quick push-in: the picture grows by <paramref name="amount"/> (1.3 = 30% bigger) in a tenth of a second and eases back over <paramref name="length"/> seconds. For hits and drops.</summary>
    public Project PunchIn(Guid id, double timelineTime, double amount = 1.3, double length = 0.45)
    {
        var item = Find(id);
        if (item is null) return this;
        double at = Math.Clamp(timelineTime - item.Start, 0, item.Length), end = Math.Min(item.Length, at + length);
        if (end - at < 0.12) return this;
        double v = item.Props.ValueAt(KeyProp.Scale, at), back = item.Props.ValueAt(KeyProp.Scale, end);
        return SetProps(id, o => o.WithKeyBurst(KeyProp.Scale, at, end, new[] { (at, v), (at + 0.1, v * amount), (end, back) }));
    }

    /// <summary>A shake: the picture jitters from side to side and settles over <paramref name="length"/> seconds. <paramref name="strength"/> is a share of the frame.</summary>
    public Project Shake(Guid id, double timelineTime, double strength = 0.025, double length = 0.5)
    {
        var item = Find(id);
        if (item is null) return this;
        double at = Math.Clamp(timelineTime - item.Start, 0, item.Length), end = Math.Min(item.Length, at + length);
        if (end - at < 0.12) return this;
        double x0 = item.Props.ValueAt(KeyProp.PosX, at), y0 = item.Props.ValueAt(KeyProp.PosY, at);
        var xs = new List<(double, double)> { (at, x0) }; var ys = new List<(double, double)> { (at, y0) };
        var rng = new Random(7);
        int n = (int)((end - at) / 0.05);
        for (int i = 1; i < n; i++)
        {
            double fade = 1 - (double)i / n, t = at + i * 0.05;
            xs.Add((t, x0 + (i % 2 == 0 ? 1 : -1) * strength * fade * (0.6 + 0.4 * rng.NextDouble())));
            ys.Add((t, y0 + (i % 3 == 0 ? 1 : -1) * strength * 0.7 * fade * (0.6 + 0.4 * rng.NextDouble())));
        }
        xs.Add((end, item.Props.ValueAt(KeyProp.PosX, end))); ys.Add((end, item.Props.ValueAt(KeyProp.PosY, end)));
        return SetProps(id, o => o.WithKeyBurst(KeyProp.PosX, at, end, xs).WithKeyBurst(KeyProp.PosY, at, end, ys));
    }

    public Project RemoveKey(Guid id, KeyProp prop, double timelineTime)
    {
        var item = Find(id);
        return item is null ? this : SetProps(id, o => o.WithoutKey(prop, timelineTime - item.Start));
    }

    public Project ClearKeys(Guid id, KeyProp prop) => SetProps(id, o => o.ClearKeys(prop));

    /// <summary>Links items together (a picture and its sound), so they are selected, moved, cut and deleted as one.</summary>
    public Project Link(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count < 2) return this;
        var link = Guid.NewGuid();
        var p = this;
        for (int i = 0; i < p.Tracks.Count; i++)
            if (p.Tracks[i].Items.Any(it => set.Contains(it.Id)))
                p = p.Replace(i, l => l.Select(it => set.Contains(it.Id) ? it with { LinkId = link } : it).ToImmutableList());
        return p;
    }

    /// <summary>The ids of everything linked with an item, the item included (just the item when it is not linked).</summary>
    public List<Guid> GroupOf(Guid id)
    {
        var item = Find(id);
        if (item is null) return new List<Guid>();
        return item.LinkId is { } link ? Items.Where(i => i.LinkId == link).Select(i => i.Id).ToList() : new List<Guid> { id };
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
        foreach (var t in Items.Where(i => !ignore.Contains(i.Id)).SelectMany(i => new[] { i.Start, i.End }).Concat(Marks).Append(0))
            if (Math.Abs(t - time) <= within && (best is null || Math.Abs(t - time) < Math.Abs(best.Value - time))) best = t;
        return best;
    }
}

/// <summary>An edit as it is saved: the project plus what is needed to open it again (its name, the picture's shape, how long each source file is).</summary>
/// <param name="Pool">The files in the media pool (the full paths, or relative ones in a portable edit). Null in edits saved before the pool was kept.</param>
public sealed record ProjectFile(int Version, string Name, double Aspect, Project Project, Dictionary<string, double> SourceLengths, List<string>? Pool = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a saved edit; null if the text is not one.</summary>
    public static ProjectFile? FromJson(string json)
    {
        try { var f = JsonSerializer.Deserialize<ProjectFile>(json, Options); return f is { Project: not null } ? f.WithoutSpeedKeys() : null; }
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
        Pool = Pool?.Select(map).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };

    /// <summary>Turns the file paths that are relative (as in a portable edit) into full paths, counting from the folder the edit file is in.</summary>
    public ProjectFile ResolveFrom(string folder) => MapSources(s => System.IO.Path.IsPathRooted(s) ? s : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, s)));

    /// <summary>The same edit without speed keyframes (edits saved while speed could be animated: the speed stays at its plain value).</summary>
    public ProjectFile WithoutSpeedKeys()
    {
        if (!Project.Items.Any(i => i.Props.Keys is { } ks && ks.Any(k => k.Prop == KeyProp.Speed))) return this;
        return this with
        {
            Project = Project with
            {
                Tracks = Project.Tracks.Select(t => t with
                {
                    Items = t.Items.Select(i => i.Props.Keys is { } ks && ks.Any(k => k.Prop == KeyProp.Speed)
                        ? i with { Props = i.Props with { Keys = ks.Where(k => k.Prop != KeyProp.Speed).ToImmutableList() is { Count: > 0 } kept ? kept : null } }
                        : i).ToImmutableList(),
                }).ToImmutableList(),
            },
        };
    }

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
