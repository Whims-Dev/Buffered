using Buffered.UI;
int fails = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "pass  " : "FAIL  ") + what); if (!ok) fails++; }
double dur = 7.05;
PlayheadResult Tick(PlayheadModel m, double now, bool playing, double reported, double cur, bool stalled = false, double limit = 7.05)
    => m.Update(new PlayheadInput(now, playing, reported, dur, limit, stalled, cur));

// 1. Playing: follows the player, gliding between its reports, never going backwards by a little
{ var m = new PlayheadModel(); double cur = 0, t = 0; var seen = new List<double>();
  for (int i = 0; i < 100; i++) { t += 0.016; double reported = Math.Floor(t / 0.1) * 0.1; var r = Tick(m, t, true, reported, cur); if (r.Position is { } p) cur = p; seen.Add(cur); }
  bool mono = Enumerable.Range(1, seen.Count - 1).All(i => seen[i] >= seen[i - 1] - 1e-9);
  Check(mono && Math.Abs(cur - t) < 0.3, $"plays forward smoothly (ended at {cur:0.00} for {t:0.00} s)"); }

// 2. Drop then play: the player still reports the old spot for 0.3 s; the playhead must not jump back to it
{ var m = new PlayheadModel(); double cur = 5.0, now = 10; m.Hold(5.0, now, 0.7); var seen = new List<double>();
  for (int i = 0; i < 40; i++) { now += 0.016; double reported = now - 10 < 0.3 ? 1.0 : 5.0 + (now - 10.3); var r = Tick(m, now, true, reported, cur); if (r.Position is { } p) cur = p; seen.Add(cur); }
  Check(seen.Min() >= 4.99, $"a stale position after a drop does not pull the playhead back (lowest {seen.Min():0.00})");
  Check(Math.Abs(seen[^1] - (5.0 + (now - 10.3))) < 0.35, $"and it ends up with the player ({seen[^1]:0.00})"); }

// 3. Paused: the playhead stays where it was put even if the player reports something close but different
{ var m = new PlayheadModel(); m.Put(3.0); var r = Tick(m, 50, false, 3.08, 3.0);
  Check(r.Position is null, "paused: leaves the playhead alone when the player is nearly there"); }

// 3b. Paused after a jump to the very end: the player reads zero, the playhead must stay at the end
{ var m = new PlayheadModel(); m.Put(7.05); var r = Tick(m, 60, false, 0.0, 7.05);
  Check(r.Position is null, "paused: a zero report from the player is ignored"); }

// 4. A jump while settling: the playhead stands at the spot the whole time, then no carry-forward
{ var m = new PlayheadModel(); double cur = 2.0, now = 20; m.BeginSettle(4.0); var seen = new List<double>();
  for (int i = 0; i < 31; i++) { now += 0.016; var r = Tick(m, now, true, 2.0, cur); if (r.Position is { } p) cur = p; seen.Add(cur); }
  Check(seen.All(x => Math.Abs(x - 4.0) < 1e-9), "while settling the playhead stands exactly at the jump spot");
  m.EndSettle(now); now += 0.016; var r2 = Tick(m, now, true, 4.0, cur);
  Check(r2.Position is { } p2 && Math.Abs(p2 - 4.0) < 0.05, $"right after the wait it does not jump ahead ({r2.Position:0.00})"); }

// 4b. After the wait the playhead follows the player closely (it once led it by a quarter of a second for the rest of the clip)
{ var m = new PlayheadModel(); double cur = 4.0, now = 40; m.Hold(4.0, now, 0.7); m.BeginSettle(4.0);
  for (int i = 0; i < 31; i++) { now += 0.016; var r0 = Tick(m, now, true, 4.0, cur); if (r0.Position is { } q0) cur = q0; }
  m.EndSettle(now); double video = 4.0, worst = 0;
  for (int i = 0; i < 120; i++) { now += 0.016; video += 0.016; var r = Tick(m, now, true, video, cur); if (r.Position is { } q) cur = q; if (i > 5) worst = Math.Max(worst, Math.Abs(cur - video)); }
  Check(worst < 0.1, $"after a wait the playhead stays with the picture (worst gap {worst:0.000} s)"); }

// 5. Ending: after running off the end the player reads 0; the playhead must not jump to the start
{ var m = new PlayheadModel(); m.Put(7.0); var r = Tick(m, 80, true, 0.0, 7.0);
  Check(r.Position is null && !r.Follow, "a zero report just after the end is ignored");
  m.Ended(dur); var r2 = Tick(m, 80.1, false, 0.0, 7.0);
  Check(r2.Position is { } e && Math.Abs(e - dur) < 1e-9, "after the end the playhead sits at the end"); }

// 6. Stalled picture (player decoding): playhead stands, then goes on when the picture does
{ var m = new PlayheadModel(); double cur = 3.0, now = 30; Tick(m, now, true, 3.0, cur); m.Put(3.0);
  var seen = new List<double>(); for (int i = 0; i < 30; i++) { now += 0.016; var r = Tick(m, now, true, 3.0, cur, stalled: true); if (r.Position is { } p) cur = p; seen.Add(cur); }
  Check(seen.Max() < 3.3, $"a stalled picture does not let the playhead creep far (highest {seen.Max():0.00})"); }

// 7. A trim end limits the playhead
{ var m = new PlayheadModel(); double cur = 4.9; var r = Tick(m, 90, true, 5.4, cur, limit: 5.0);
  Check(r.Position is { } q && q <= 5.0 + 1e-9, "never past the trim end"); }

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} FAILED"); return fails;
