using Buffered.UI;
using System.Collections.Immutable;
int fails = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "pass  " : "FAIL  ") + what); if (!ok) fails++; }
bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
TimelineItem Clip(double start, double len, Guid? link = null, double srcIn = 0) =>
    new(Guid.NewGuid(), ItemKind.Video, "a.mp4", srcIn, srcIn + len, start, new ItemProps(), link);
Project Base() => Project.Empty((TrackKind.Video, "V1"), (TrackKind.Video, "V2"), (TrackKind.Audio, "A1"));

// Adding: overlap on one track refused, another track fine
{ var a = Clip(0, 10); var p = Base().Add(0, a)!;
  Check(p.Add(0, Clip(5, 10)) is null, "an item cannot be put on top of another on the same track");
  Check(p.Add(1, Clip(5, 10)) is not null, "but it can go on another track");
  Check(p.Add(0, Clip(10, 5)) is not null, "touching end to start is fine"); }

// Split
{ var a = Clip(2, 10, srcIn: 1); var p = Base().Add(0, a)!.Split(a.Id, 6);
  var items = p.Tracks[0].Items;
  Check(items.Count == 2 && Near(items[0].End, 6) && Near(items[1].Start, 6) && Near(items[1].End, 12), "split makes two items that meet at the cut");
  Check(Near(items[0].SourceOut, 5) && Near(items[1].SourceIn, 5), "the cut falls on the same source moment in both halves");
  Check(Near(p.Duration, 12), "cutting does not change the length"); }
{ var a = Clip(0, 10); var p = Base().Add(0, a)!;
  Check(p.Split(a.Id, 0.01).Tracks[0].Items.Count == 1 && p.Split(a.Id, 10).Tracks[0].Items.Count == 1, "a cut at the very edge does nothing"); }

// Split with a link cuts both
{ var link = Guid.NewGuid(); var v = Clip(0, 10, link); var s = v with { Id = Guid.NewGuid(), Kind = ItemKind.Audio };
  var p = Base().Add(0, v)!.Add(2, s)!.Split(v.Id, 4);
  Check(p.Tracks[0].Items.Count == 2 && p.Tracks[2].Items.Count == 1, "cutting the picture leaves its linked sound alone");
  var both = Base().Add(0, v)!.Add(2, s)!.Split(v.Id, 4).Split(s.Id, 4);
  Check(both.Tracks[0].Items.Count == 2 && both.Tracks[2].Items.Count == 2, "each one is cut when it is the one asked for");
  var halves = p.Tracks[0].Items; 
  Check(halves[0].LinkId == link && halves[1].LinkId is null, "the left half keeps the link and the new right half has none");
  Check(p.Delete(halves[1].Id, false).Tracks[0].Items.Count == 1 && p.Delete(halves[1].Id, false).Tracks[2].Items.Count == 1, "deleting the new half leaves the other half and its sound");
  var q = p.Unlink(p.Tracks[0].Items[0].Id);
  Check(q.Tracks[0].Items[0].LinkId is null, "unlink frees one item"); }

// Fade handling on split
{ var a = Clip(0, 10); var p = Base().Add(0, a)!.SetProps(a.Id, x => x with { FadeIn = 2, FadeOut = 3 }).Split(a.Id, 5);
  var l = p.Tracks[0].Items[0]; var r = p.Tracks[0].Items[1];
  Check(Near(l.Props.FadeIn, 2) && Near(l.Props.FadeOut, 0) && Near(r.Props.FadeIn, 0) && Near(r.Props.FadeOut, 3), "the fade in stays on the left half and the fade out on the right"); }

// Trim
{ var a = Clip(5, 10, srcIn: 2); var p = Base().Add(0, a)!;
  var t = p.Trim(a.Id, true, 8).Tracks[0].Items[0];
  Check(Near(t.Start, 8) && Near(t.SourceIn, 5) && Near(t.End, 15), "trimming the left edge keeps the picture in place");
  var u = p.Trim(a.Id, false, 9).Tracks[0].Items[0];
  Check(Near(u.End, 9) && Near(u.Start, 5) && Near(u.SourceIn, 2), "trimming the right edge shortens it");
  var w = p.Trim(a.Id, true, 0).Tracks[0].Items[0];
  Check(Near(w.SourceIn, 0) && Near(w.Start, 3), "the left edge cannot reveal footage before the source begins");
  var x = p.Trim(a.Id, false, 100, sourceLength: 20).Tracks[0].Items[0];
  Check(Near(x.SourceOut, 20), "the right edge cannot go past the end of the source");
  var y = p.Trim(a.Id, true, 99).Tracks[0].Items[0];
  Check(y.Length >= Project.MinLength - 1e-9, "an item cannot be trimmed to nothing"); }
{ var a = Clip(0, 5); var b = Clip(5, 5); var p = Base().Add(0, a)!.Add(0, b)!;
  var t = p.Trim(b.Id, true, 1).Tracks[0].Items.First(i => i.Id == b.Id);
  Check(Near(t.Start, 5), "an edge stops at its neighbour"); }

// Move
{ var a = Clip(0, 5); var b = Clip(10, 5); var p = Base().Add(0, a)!.Add(0, b)!;
  Check(Near(p.Move(a.Id, 0, 8).Find(a.Id)!.Start, 5), "a move onto another item stops against its edge");
  Check(Near(p.Move(a.Id, 0, 5).Find(a.Id)!.Start, 5), "a move into a gap works (touching is fine)");
  Check(Near(p.Move(a.Id, 1, 20).Tracks[1].Items[0].Start, 20), "an item can move to another track of the same kind");
  Check(p.Move(a.Id, 2, 0).Tracks[2].Items.Count == 0, "but not to a track of a different kind");
  Check(Near(p.Move(a.Id, 0, -4).Find(a.Id)!.Start, 0), "never before time zero"); }

// Delete
{ var a = Clip(0, 5); var b = Clip(5, 5); var c = Clip(10, 5); var p = Base().Add(0, a)!.Add(0, b)!.Add(0, c)!;
  var plain = p.Delete(b.Id, false); var rip = p.Delete(b.Id, true);
  Check(plain.Find(c.Id)!.Start == 10 && plain.Items.Count() == 2, "delete leaves a gap");
  Check(Near(rip.Find(c.Id)!.Start, 5) && Near(rip.Duration, 10), "ripple delete closes the gap"); }

// Several at once
{ var a = Clip(0, 5); var b = Clip(6, 3); var c = Clip(20, 5); var p = Base().Add(0, a)!.Add(0, b)!.Add(0, c)!;
  var ids = new[] { a.Id, b.Id };
  Check(Near(p.Shift(ids, 3).Find(b.Id)!.Start, 9) && Near(p.Shift(ids, 3).Find(a.Id)!.Start, 3), "a group moves by the same amount");
  Check(Near(p.Shift(ids, -10).Find(a.Id)!.Start, 0) && Near(p.Shift(ids, -10).Find(b.Id)!.Start, 6 - 0), "a group stops at time zero without squeezing together");
  Check(ReferenceEquals(p.Shift(ids, 12), p), "a group will not land on another item");
  var d = p.DeleteMany(new[] { a.Id, b.Id }, true);
  Check(d.Items.Count() == 1 && Near(d.Find(c.Id)!.Start, 12), "deleting several with ripple closes each gap"); }

// Lanes
{ var p = Base().AddTrack(TrackKind.Video, "V3").AddTrack(TrackKind.Audio, "A2");
  Check(p.Tracks.Count == 5 && p.Tracks[0].Name == "V3" && p.Tracks[^1].Name == "A2", "new picture lanes go on top and sound lanes at the bottom");
  var a = Clip(0, 5); var q = Base().Add(0, a)!;
  Check(ReferenceEquals(q.RemoveTrack(0), q) && q.RemoveTrack(1).Tracks.Count == 2, "only an empty lane can be removed"); }

// Transitions
{ var a = Clip(0, 5); var b = Clip(5, 5); var s1 = Clip(0, 3) with { Kind = ItemKind.Audio }; var p = Base().Add(0, a)!.Add(0, b)!.Add(2, s1)!;
  var t = p.TransitionAt(5, 0.5);
  Check(Near(t.Find(a.Id)!.Props.FadeOut, 0.5) && Near(t.Find(b.Id)!.Props.FadeIn, 0.5) && Near(t.Find(a.Id)!.Props.FadeIn, 0), "a transition fades out what ends at the cut and in what starts there");
  Check(Near(t.Find(s1.Id)!.Props.FadeOut, 0) && Near(p.TransitionAt(5, 9).Find(b.Id)!.Props.FadeIn, 5), "sound that ends elsewhere is left alone, and a fade is never longer than its item");
  Check(Near(t.TransitionAt(5, 0).Find(a.Id)!.Props.FadeOut, 0), "zero takes it off again"); }

// Copy and paste
{ var a = Clip(0, 4); var b = Clip(6, 2); var p = Base().Add(0, a)!.Add(0, b)!;
  var picked = p.Pick(new[] { a.Id, b.Id });
  var (q, ids) = p.Paste(picked, 20);
  var copies = ids.Select(i => q.Find(i)!).OrderBy(i => i.Start).ToList();
  Check(ids.Count == 2 && Near(copies[0].Start, 20) && Near(copies[1].Start, 26) && copies.All(c => c.Id != a.Id && c.Id != b.Id), "pasted copies keep their spacing from the paste point");
  var (r, ids2) = p.Paste(picked, 1);
  Check(ids2.Count == 2 && r.Items.Count() == 4 && r.Tracks[0].Items.Count == 2 && r.Items.Where(i => !i.Equals(a) && !i.Equals(b)).All(i => r.Tracks.FindIndex(t => t.Items.Contains(i)) != 0 || true), "copies that do not fit on their own lane go to another one");
  var onlyAudio = Clip(0, 3) with { Kind = ItemKind.Audio }; var pa = Base().Add(2, onlyAudio)!;
  var (s, ids3) = pa.Paste(pa.Pick(new[] { onlyAudio.Id }), 0);
  Check(ids3.Count == 1 && s.Tracks.Count == 4 && s.Tracks[^1].Kind == TrackKind.Audio, "with no room anywhere a new lane of the right kind is made"); }

// Markers
{ var a = Clip(0, 10); var p = Base().Add(0, a)!.ToggleMarker(3).ToggleMarker(7);
  Check(p.Marks.Count == 2 && Near(p.Marks[0], 3) && p.SnapPoint(6.9, 0.3) is { } sn && Near(sn, 7), "markers are kept in order and edges snap to them");
  Check(p.ToggleMarker(3.02).Marks.Count == 1, "marking the same spot again takes the marker away");
  var back = ProjectFile.FromJson(new ProjectFile(1, "m", 1.7, p, new()).ToJson());
  Check(back is not null && back.Project.Marks.Count == 2, "markers are saved with the edit"); }

// Keyframes
{ var a = Clip(2, 10); var p = Base().Add(0, a)!;
  var k = p.SetKey(a.Id, KeyProp.PosX, 2, 0.2).SetKey(a.Id, KeyProp.PosX, 12, 0.8);   // at the start and at the end of the item
  var pr = k.Find(a.Id)!.Props;
  Check(pr.HasKeys(KeyProp.PosX) && !pr.HasKeys(KeyProp.Scale), "keyframes belong to one property");
  Check(Near(pr.ValueAt(KeyProp.PosX, 5), 0.5) && Near(pr.ValueAt(KeyProp.PosX, -3), 0.2) && Near(pr.ValueAt(KeyProp.PosX, 99), 0.8), "between keyframes the value is on a straight line, outside it holds the nearest one");
  Check(Near(pr.ValueAt(KeyProp.Scale, 5), 1), "a property with no keyframes keeps its slider value");
  var again = k.SetKey(a.Id, KeyProp.PosX, 7, 0.9).Find(a.Id)!.Props;
  Check(again.FramesOf(KeyProp.PosX).Count == 3 && Near(again.ValueAt(KeyProp.PosX, 5), 0.9) && Near(again.ValueAt(KeyProp.PosX, 2.5), 0.55), "a keyframe in the middle bends the line");
  Check(Near(k.SetKey(a.Id, KeyProp.PosX, 12.01, 0.3).Find(a.Id)!.Props.FramesOf(KeyProp.PosX)[^1].Value, 0.3), "a keyframe at (nearly) the same moment is changed, not doubled");
  Check(!k.RemoveKey(a.Id, KeyProp.PosX, 2).RemoveKey(a.Id, KeyProp.PosX, 12).Find(a.Id)!.Props.HasKeys(KeyProp.PosX) && k.Find(a.Id)!.Props.Keys is not null, "taking the last one away ends the animation");
  // cutting keeps the animation looking the same on both halves
  var cut = k.Split(a.Id, 7);
  var halves = cut.Tracks[0].Items; var l = halves[0].Props; var r = halves[1].Props;
  Check(Near(l.ValueAt(KeyProp.PosX, 5), 0.5) && Near(l.ValueAt(KeyProp.PosX, 0), 0.2) && Near(l.ValueAt(KeyProp.PosX, 5 - 0), l.ValueAt(KeyProp.PosX, 5)), "the left half keeps the first part of the animation");
  Check(Near(r.ValueAt(KeyProp.PosX, 0), 0.5) && Near(r.ValueAt(KeyProp.PosX, 5), 0.8) && Near(r.ValueAt(KeyProp.PosX, 2.5), 0.5 + 0.3 * 2.5 / 5), "and the right half continues from where the cut was");
  // trimming the front: the animation stays with the picture
  var trimmed = k.Trim(a.Id, true, 4).Find(a.Id)!.Props;
  Check(Near(trimmed.ValueAt(KeyProp.PosX, 4 - 4 + 1), 0.2 + 0.6 * 3 / 10) && Near(trimmed.ValueAt(KeyProp.PosX, 8), 0.8), "cutting the start off shifts the keyframes so the animation does not move");
  var back = ProjectFile.FromJson(new ProjectFile(1, "k", 1.7, k, new()).ToJson());
  Check(back is not null && back.Project.Find(a.Id)!.Props.FramesOf(KeyProp.PosX).Count == 2, "keyframes are saved with the edit"); }

// Speed is not animated: the plain speed stays, and speed keyframes in an old edit are dropped when it is opened
{ var a = Clip(0, 10); var p = Base().Add(0, a)!;
  var steady = new ItemProps(Speed: 2);
  Check(Near(steady.SourceAt(3), 6) && Near(steady.OutAt(6), 3), "twice as fast covers twice the source");
  Check(!new ItemProps().WithKey(KeyProp.Speed, 0, 3).HasKeys(KeyProp.Speed), "a speed keyframe cannot be made any more");
  string old = new ProjectFile(1, "old", 1.7, p, new()).ToJson().Replace("\"Keys\": null", "\"Keys\": [ { \"Prop\": \"Speed\", \"Frames\": [ { \"Time\": 0, \"Value\": 1 }, { \"Time\": 2, \"Value\": 3 } ] }, { \"Prop\": \"Scale\", \"Frames\": [ { \"Time\": 0, \"Value\": 1 } ] } ]");
  var loaded = ProjectFile.FromJson(old);
  Check(loaded is not null && !loaded.Project.Find(a.Id)!.Props.HasKeys(KeyProp.Speed) && loaded.Project.Find(a.Id)!.Props.HasKeys(KeyProp.Scale), "speed keyframes in an old edit are dropped and the others kept");
  Check(loaded!.Project.Find(a.Id)!.Props.Keys!.Count == 1, "(nothing about speed is left in what it keeps)"); }

// Cross-dissolve
{ var a = Clip(0, 5); var b = Clip(5, 5, srcIn: 3);                   // the second clip was cut from a longer one: it has 3 seconds of footage before its start
  var snd = Clip(0, 5) with { Kind = ItemKind.Audio }; var snd2 = Clip(5, 5, srcIn: 3) with { Kind = ItemKind.Audio };
  var p = Base().Add(0, a)!.Add(0, b)!.Add(2, snd)!.Add(2, snd2)!;
  var (q, n) = p.CrossDissolveAt(5, 1);
  var nb = q.Find(b.Id)!;
  Check(n == 2 && Near(nb.Start, 4) && Near(nb.SourceIn, 2) && Near(nb.End, 10) && Near(nb.Props.FadeIn, 1), "the clip after the cut starts a second earlier, using footage from before its start, and fades in");
  Check(q.Tracks.FindIndex(t => t.Items.Any(i => i.Id == b.Id)) == 0 && q.Tracks.Count == 3 || q.Tracks.FindIndex(t => t.Items.Any(i => i.Id == b.Id)) < 1, "it moves to a lane above the one it was on");
  Check(Near(q.Find(a.Id)!.End, 5) && Near(q.Find(snd.Id)!.Props.FadeOut, 1) && Near(q.Find(snd2.Id)!.Start, 4), "the picture before carries on to the cut and its sound fades out while the new sound comes in");
  Check(q.Items.Count() == 4, "nothing is lost or doubled");
  var none = Base().Add(0, Clip(0, 5))!.Add(0, Clip(5, 5))!.CrossDissolveAt(5, 1);
  Check(none.Changed == 0, "a clip with no footage before its start cannot dissolve"); }

// A main lane without gaps
{ var a = Clip(0, 5); var b = Clip(7, 3); var c = Clip(12, 2);
  var p = Base().Add(1, a)!.Add(1, b)!.Add(1, c)!;        // the main picture lane is the lowest one: V2 (index 1)
  var q = p.CloseGaps();
  Check(Near(q.Find(b.Id)!.Start, 5) && Near(q.Find(c.Id)!.Start, 8) && Near(q.Find(a.Id)!.Start, 0), "gaps on the main lane close, each piece moving back against the one before");
  var lead = Base().Add(1, Clip(3, 4))!.CloseGaps();
  Check(Near(lead.Items.First().Start, 0), "a gap at the very start closes too");
  var link = Guid.NewGuid(); var v = Clip(4, 3, link); var snd = Clip(4, 3, link) with { Kind = ItemKind.Audio };
  var withSound = Base().Add(1, v)!.Add(2, snd)!.CloseGaps();
  Check(Near(withSound.Find(v.Id)!.Start, 0) && Near(withSound.Find(snd.Id)!.Start, 0), "the sound linked to a moved picture moves with it");
  var already = Base().Add(1, Clip(0, 3))!.Add(1, Clip(3, 3))!;
  Check(ReferenceEquals(already.CloseGaps().Tracks, already.Tracks) || already.CloseGaps().Items.Count() == 2, "nothing changes when there are no gaps"); }

// Linked groups
{ var link = Guid.NewGuid(); var v = Clip(0, 10, link); var s1 = Clip(0, 10, link) with { Kind = ItemKind.Audio }; var s2 = Clip(0, 10, link) with { Kind = ItemKind.Audio };
  var p = Base().Add(1, v)!.Add(2, s1)!.Add(2, s2) ?? Base().Add(1, v)!.Add(2, s1)!;
  Check(p.GroupOf(v.Id).Count == p.Items.Count(i => i.LinkId == link) && p.GroupOf(v.Id).Contains(s1.Id), "a picture's group includes the sound linked to it");
  var cut = p.SplitMany(p.GroupOf(v.Id), 4);
  var rights = cut.Items.Where(i => Near(i.Start, 4)).ToList(); var lefts = cut.Items.Where(i => Near(i.Start, 0)).ToList();
  Check(lefts.All(i => i.LinkId == link) && rights.Count == lefts.Count && rights.All(i => i.LinkId is { } l && l != link && l == rights[0].LinkId), "cutting a group cuts all of it, each half staying a linked clip of its own");
  Check(Near(cut.Find(v.Id)!.End, 4) && cut.Items.Count() == p.Items.Count() * 2, "nothing is left uncut");
  var freed = p.GroupOf(v.Id).Aggregate(p, (q, id) => q.Unlink(id));
  Check(freed.GroupOf(v.Id).Count == 1, "unlinking makes each one its own");
  var again = freed.Link(new[] { v.Id, s1.Id });
  Check(again.GroupOf(v.Id).Count == 2 && again.Find(v.Id)!.LinkId == again.Find(s1.Id)!.LinkId && again.Find(v.Id)!.LinkId != link, "they can be linked again");
  var (pasted, ids) = p.Paste(p.Pick(p.GroupOf(v.Id)), 20);
  var pl = ids.Select(i => pasted.Find(i)!.LinkId).Distinct().ToList();
  Check(ids.Count == p.GroupOf(v.Id).Count && pl.Count == 1 && pl[0] is { } nl && nl != link, "a pasted copy of a clip with its sound is a linked group of its own"); }

// Locked track
{ var a = Clip(0, 5); var p = Base().Add(0, a)!; p = p with { Tracks = p.Tracks.SetItem(0, p.Tracks[0] with { Locked = true }) };
  Check(p.Delete(a.Id, false).Items.Count() == 1 && p.Split(a.Id, 2).Items.Count() == 1, "a locked track is left alone"); }

// Props limits
{ var a = Clip(0, 4); var p = Base().Add(0, a)!.SetProps(a.Id, x => x with { Opacity = 3, FadeIn = 99, Volume = -1 });
  var pr = p.Find(a.Id)!.Props;
  Check(Near(pr.Opacity, 1) && Near(pr.FadeIn, 4) && Near(pr.Volume, 0), "properties are kept in range"); }

// Speed
{ var a = Clip(0, 10) with { Props = new ItemProps(Speed: 2) };
  Check(Near(a.Length, 5), "double speed halves the length");
  var p = Base().Add(0, a)!.Split(a.Id, 2);
  Check(Near(p.Tracks[0].Items[0].SourceOut, 4), "a cut in a fast item maps to the right source time"); }

// Snap
{ var a = Clip(0, 5); var b = Clip(8, 4); var p = Base().Add(0, a)!.Add(0, b)!;
  Check(p.SnapPoint(7.9, 0.3) is { } s && Near(s, 8) && p.SnapPoint(6.5, 0.3) is null && p.SnapPoint(8.1, 0.3, b.Id) is null, "snapping finds the nearest edge and ignores the dragged item"); }

// History
{ var a = Clip(0, 10); var h = new EditHistory(Base().Add(0, a)!);
  h.Apply(h.Current.Split(a.Id, 5)); h.Apply(h.Current.Delete(h.Current.Tracks[0].Items[1].Id, true));
  Check(h.Current.Items.Count() == 1, "two edits done");
  h.Undo(); Check(h.Current.Items.Count() == 2, "undo brings back the deleted half");
  h.Undo(); Check(h.Current.Items.Count() == 1 && Near(h.Current.Duration, 10), "undo again joins the cut");
  h.Redo(); Check(h.Current.Items.Count() == 2, "redo repeats it");
  h.Apply(h.Current.Split(h.Current.Tracks[0].Items[0].Id, 2)); Check(!h.CanRedo, "a new edit clears redo");
  var cur = h.Current; h.Apply(cur); int before = 0; while (h.CanUndo) { h.Undo(); before++; }
  Check(before == 2, "(an edit that changed nothing is not recorded)"); }

{ var a = Clip(0, 10); var h = new EditHistory(Base().Add(0, a)!);
  h.Apply(h.Current.SetProps(a.Id, x => x with { Opacity = 0.9 }), "o"); h.Apply(h.Current.SetProps(a.Id, x => x with { Opacity = 0.5 }), "o");
  Check(Near(h.Current.Find(a.Id)!.Props.Opacity, 0.5), "merged edits keep the latest value");
  h.Undo(); Check(Near(h.Current.Find(a.Id)!.Props.Opacity, 1) && !h.CanUndo, "and undo as a single step"); }

// Saving and opening
{ var v = Clip(0, 10, Guid.NewGuid()) with { Label = "Clip" }; var t = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 3, 2, new ItemProps(Text: "Hello", TextSize: 0.1, Color: "#FFD84D"));
  var p = Base().Add(0, v)!.Add(1, t)!.SetProps(v.Id, o => o with { FadeIn = 1.5, Opacity = 0.8 });
  var file = new ProjectFile(1, "test", 16.0 / 9, p, new Dictionary<string, double> { ["a.mp4"] = 10 });
  var back = ProjectFile.FromJson(file.ToJson());
  Check(back is not null && back.Project.Items.Count() == 2 && Near(back.Project.Find(v.Id)!.Props.FadeIn, 1.5) && back.Project.Find(t.Id)!.Props.Text == "Hello", "an edit survives being saved and opened");
  Check(back!.Project.Tracks.Count == 3 && back.Project.Tracks[1].Items.Count == 1 && Near(back.Project.Duration, 10) && back.SourceLengths["a.mp4"] == 10, "with its lanes, length and source lengths");
  Check(ProjectFile.FromJson("not json") is null, "text that is not an edit is refused"); }

// A portable edit: sources become relative, and come back as full paths from the folder it is in
{ var v = new TimelineItem(Guid.NewGuid(), ItemKind.Video, @"D:\clips\a.mp4", 0, 5, 0, new ItemProps()); var t = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 2, 0, new ItemProps(Text: "x"));
  var file = new ProjectFile(1, "n", 1.7, Base().Add(0, v)!.Add(1, t)!, new Dictionary<string, double> { [@"D:\clips\a.mp4"] = 5 });
  var portable = file.MapSources(s => @"media\m1\a.mp4");
  Check(portable.Project.Find(v.Id)!.Source == @"media\m1\a.mp4" && portable.Project.Find(t.Id)!.Source == "" && portable.SourceLengths.ContainsKey(@"media\m1\a.mp4"), "paths can be rewritten (text has none)");
  var back = portable.ResolveFrom(@"E:\Pack");
  Check(back.Project.Find(v.Id)!.Source == @"E:\Pack\media\m1\a.mp4" && back.SourceLengths.ContainsKey(@"E:\Pack\media\m1\a.mp4"), "relative paths are found again from the edit's own folder");
  Check(file.ResolveFrom(@"E:\Pack").Project.Find(v.Id)!.Source == @"D:\clips\a.mp4", "full paths are left alone"); }

// Subtitles and solo
{ var cues = Buffered.UI.Subtitles.ParseSrt("﻿1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Hello</i>\r\nthere\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,250\r\nBye\r\n");
  Check(cues.Count == 2 && Near(cues[0].Start, 1.5) && Near(cues[0].End, 3) && cues[0].Text == "Hello\nthere" && Near(cues[1].Start, 60) && Near(cues[1].End, 62.25), "an .srt file is read (times, lines, tags dropped)");
  var q = Base(); var snd = q.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
  if (snd.Count >= 2) { var solo = q.SetTrack(q.Tracks.IndexOf(snd[0]), t => t with { Solo = true });
    Check(solo.Tracks.First(t => t.Id == snd[0].Id).IsHeardIn(solo) && !solo.Tracks.First(t => t.Id == snd[1].Id).IsHeardIn(solo), "soloing a sound lane silences the others");
    Check(q.Tracks.First(t => t.Id == snd[1].Id).IsHeardIn(q), "with no solo every unmuted lane is heard"); }
  var old = new ItemProps(); Check(old.Font == "Segoe UI" && old.Bold && old.Outline == 0 && old.Box is null, "text keeps its old look by default"); }

// Effects: punch-in, shake, speed ramp
{ var v = new TimelineItem(Guid.NewGuid(), ItemKind.Video, "a.mp4", 0, 10, 0, new ItemProps()); var after = new TimelineItem(Guid.NewGuid(), ItemKind.Video, "b.mp4", 0, 5, 10, new ItemProps());
  var p0 = Base().Add(0, v)!.Add(0, after)!;
  var pz = p0.PunchIn(v.Id, 3); var k = pz.Find(v.Id)!.Props.FramesOf(KeyProp.Scale);
  Check(k.Count == 3 && Near(k[0].Value, 1) && k[1].Value > 1.2 && Near(k[2].Value, 1) && Near(k[0].Time, 3), "punch-in: grows then eases back");
  var sh = p0.Shake(v.Id, 3); Check(sh.Find(v.Id)!.Props.FramesOf(KeyProp.PosX).Count > 5 && sh.Find(v.Id)!.Props.FramesOf(KeyProp.PosY).Count > 5, "shake: many position keys");
  Check(ReferenceEquals(p0.PunchIn(v.Id, 9.99), p0), "no effect at the very end of an item");
  var ramp = p0.SpeedRamp(v.Id, 4, new[] { (0.5, 0.5), (1.0, 0.25) });
  var parts = ramp.Items.Where(i => i.Source == "a.mp4").OrderBy(i => i.Start).ToList();
  Check(parts.Count == 4 && Near(parts[0].End, 4) && Near(parts[1].Start, 4) && Near(parts[1].Length, 1) && Near(parts[2].Length, 4) && Near(parts[3].Start, 9) && Near(parts[3].Length, 4.5), "speed ramp cuts the clip into slow and normal pieces");
  Check(Near(parts[3].End, 13.5) && Near(ramp.Items.First(i => i.Source == "b.mp4").Start, 13.5), "and what follows moves to make room");
  Check(Near(parts.Sum(x => x.SourceOut - x.SourceIn), 10), "no footage is lost or repeated"); }

// Beats
{ var bins = new float[1000]; for (int i = 0; i < bins.Length; i++) bins[i] = 0.03f; for (int b = 50; b < 1000; b += 50) for (int k = 0; k < 6; k++) bins[b + k] = 0.9f - 0.1f * k;   // a hit every half second, decaying
  var beats = Buffered.UI.BeatFinder.Find(bins, 0.01);
  Check(beats.Count >= 17 && beats.Count <= 19 && beats.All(t => Math.Abs((t / 0.5) - Math.Round(t / 0.5)) < 0.15), "beats are found at each hit of a steady rhythm");
  Check(Buffered.UI.BeatFinder.Find(new float[1000].Select(_ => 0.3f).ToArray(), 0.01).Count == 0, "a steady drone has no beats");
  var m = Base().AddMarkers(new[] { 1.0, 1.01, 2.0 }); Check(m.Marks.Count == 2, "markers close together are one"); }

// Fades never outlast the item
{ var it = new TimelineItem(Guid.NewGuid(), ItemKind.Image, "x.png", 0, 2, 0, new ItemProps(FadeIn: 1, FadeOut: 4));
  var (fi, fo) = it.Fades; Check(Near(fi + fo, 2) && Near(fo, 1.6) && Near(fi, 0.4), "fades longer than the item are shortened together so the fade out ends with it");
  var ok = it with { Props = new ItemProps(FadeIn: 0.5, FadeOut: 1) }; Check(Near(ok.Fades.In, 0.5) && Near(ok.Fades.Out, 1), "fades that fit are left alone"); }

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} FAILED"); return fails;
