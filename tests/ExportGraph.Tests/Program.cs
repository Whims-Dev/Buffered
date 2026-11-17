using System.Diagnostics;
using Buffered.UI;
int fails = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "pass  " : "FAIL  ") + what); if (!ok) fails++; }

var o = new ExportOptions(1280, 720, 30, 25, 21, false);
TimelineItem V(double start, double srcIn, double srcOut, string path, ItemProps? pr = null) => new(Guid.NewGuid(), ItemKind.Video, path, srcIn, srcOut, start, pr ?? new ItemProps());
var p0 = Project.Empty((TrackKind.Text, "T"), (TrackKind.Video, "V"), (TrackKind.Audio, "A"));
string clip = @"E:\Cloud Upload\Clips\VALORANT\VALORANT_2026-11-15_03-58-03.mp4";
var v1 = V(0, 0, 3, clip, new ItemProps(FadeIn: 0.5, Opacity: 0.9)); var v2 = V(4, 3, 7, clip, new ItemProps(Scale: 1.8, PosX: 0.3, PosY: 0.6));
var snd = new TimelineItem(Guid.NewGuid(), ItemKind.Audio, "pcm", 0, 3, 0, new ItemProps(Volume: 0.5, FadeIn: 1), null, "s");
var txt = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 2, 1, new ItemProps(Text: "Hi"));
var p = p0.Add(1, v1)!.Add(1, v2)!.Add(2, snd)!.Add(0, txt)!;
var plan = ExportGraph.Build(p, o, i => i.Kind == ItemKind.Audio ? "x.pcm" : null, i => "x.png", s => (1920, 1080));
Check(plan.FilterGraph.Contains("overlay") && plan.FilterGraph.Contains("fade=t=in") && plan.FilterGraph.Contains("adelay") == false && plan.HasAudio, "the graph has the picture layers, the fade and the sound");
Check(Math.Abs(plan.Duration - 8) < 1e-6, "its length is the edit's length");
Check(ExportGraph.TempoSteps(4).SequenceEqual(new[] { 2.0, 2.0 }) && ExportGraph.TempoSteps(1).Count() == 0, "big speed changes are made in steps");

// Really run it, when this machine can
bool haveFfmpeg = false;
try { using var q = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!; q.WaitForExit(); haveFfmpeg = q.ExitCode == 0; } catch { }
if (haveFfmpeg && File.Exists(clip))
{
    string dir = Path.Combine(Path.GetTempPath(), "bf-export-test"); Directory.CreateDirectory(dir);
    string pcm = Path.Combine(dir, "a.pcm"), png = Path.Combine(dir, "t.png"), outp = Path.Combine(dir, "out.mp4");
    int Run(string exe, params string[] args) { var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var a in args) psi.ArgumentList.Add(a); using var pr = Process.Start(psi)!; var err = pr.StandardError.ReadToEndAsync(); pr.StandardOutput.ReadToEnd(); pr.WaitForExit(); if (pr.ExitCode != 0) Console.WriteLine(err.Result.Trim()); return pr.ExitCode; }
    Run("ffmpeg", "-y", "-loglevel", "error", "-i", clip, "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-f", "s16le", pcm);
    Run("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red@0.6:s=1280x720,format=rgba", "-frames:v", "1", png);
    var real = ExportGraph.Build(p, o, i => i.Kind == ItemKind.Audio ? pcm : null, i => png, s => (1920, 1080));
    int code = Run("ffmpeg", ExportGraph.Arguments(real, o, outp, false).ToArray());
    Check(code == 0 && File.Exists(outp), "ffmpeg makes the video from the graph");
    var probe = new ProcessStartInfo("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{outp}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var pp = Process.Start(probe)!; string dur = pp.StandardOutput.ReadToEnd().Trim(); pp.WaitForExit();
    Check(double.TryParse(dur, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d - 8) < 0.3, $"and it is as long as the edit ({dur} s)");
}
if (haveFfmpeg && File.Exists(clip))
{
    // an animated edit: position, zoom and opacity of a video, the volume of a sound, and the zoom of some text
    string dir = Path.Combine(Path.GetTempPath(), "bf-export-test"); string pcm = Path.Combine(dir, "a.pcm"), png = Path.Combine(dir, "tight.png"), outp = Path.Combine(dir, "anim.mp4");
    int Run2(string exe, params string[] args) { var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var a in args) psi.ArgumentList.Add(a); using var pr = Process.Start(psi)!; var err = pr.StandardError.ReadToEndAsync(); pr.StandardOutput.ReadToEnd(); pr.WaitForExit(); if (pr.ExitCode != 0) Console.WriteLine(err.Result.Trim()); return pr.ExitCode; }
    Run2("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red@0.8:s=300x100,format=rgba", "-frames:v", "1", png);
    var vp = new ItemProps().WithKey(KeyProp.PosX, 0, 0.2).WithKey(KeyProp.PosX, 3, 0.8).WithKey(KeyProp.Scale, 0, 0.6).WithKey(KeyProp.Scale, 3, 1.2).WithKey(KeyProp.Opacity, 0, 0.3).WithKey(KeyProp.Opacity, 3, 1);
    var av = V(0, 0, 3, clip, vp);
    var ap = new ItemProps().WithKey(KeyProp.Volume, 0, 0).WithKey(KeyProp.Volume, 3, 1.5);
    var asn = new TimelineItem(Guid.NewGuid(), ItemKind.Audio, "pcm", 0, 3, 0, ap, null, "s");
    var tp = new ItemProps(Text: "Hi").WithKey(KeyProp.Scale, 0, 0.5).WithKey(KeyProp.Scale, 2, 1.5).WithKey(KeyProp.PosY, 0, 0.2).WithKey(KeyProp.PosY, 2, 0.8);
    var at = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 2, 0.5, tp);
    var ap0 = Project.Empty((TrackKind.Text, "T"), (TrackKind.Video, "V"), (TrackKind.Audio, "A")).Add(1, av)!.Add(2, asn)!.Add(0, at)!;
    var animated = ExportGraph.Build(ap0, o, i => i.Kind == ItemKind.Audio ? pcm : null, i => png, s => null);
    Check(animated.FilterGraph.Contains("eval=frame") && animated.FilterGraph.Contains("sendcmd") && animated.FilterGraph.Contains("if(lt(t,"), "the animated graph has expressions for position, zoom, opacity and volume");
    Check(ExportGraph.ExtraFiltersFor(ap0).Contains("sendcmd") && !ExportGraph.ExtraFiltersFor(Project.Empty()).Any(), "it says which extra filter it needs");
    int code = Run2("ffmpeg", ExportGraph.Arguments(animated, o, outp, false).ToArray());
    Check(code == 0 && File.Exists(outp), "ffmpeg makes the animated video");

    // a speed ramp: 1x at the start rising to 3x at 2 seconds, over 5 seconds of the clip (so it plays in less than 5 seconds), and text whose size is animated
    var sp = new ItemProps().WithKey(KeyProp.Speed, 0, 1).WithKey(KeyProp.Speed, 2, 3);
    var sv = V(0, 0, 5, clip, sp);                         // Length = time to play 5 seconds of source at that speed
    var tpp = new ItemProps(Text: "Hi").WithKey(KeyProp.TextSize, 0, 0.04).WithKey(KeyProp.TextSize, 2, 0.12);
    var tx = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 2, 0, tpp);
    var sproj = Project.Empty((TrackKind.Text, "T"), (TrackKind.Video, "V")).Add(1, sv)!.Add(0, tx)!;
    string spOut = Path.Combine(dir, "speed.mp4");
    var splan = ExportGraph.Build(sproj, o, _ => null, i => png, s => null);
    Check(splan.FilterGraph.Contains("sqrt(") && Math.Abs(sv.Length - (2 + (5 - 4) / 3.0)) < 1e-6, "the speed ramp gives a retiming expression, and the item is as long as the ramp says");
    int code2 = Run2("ffmpeg", ExportGraph.Arguments(splan, o, spOut, false).ToArray());
    Check(code2 == 0 && File.Exists(spOut), "ffmpeg makes the video with a speed ramp and animated text size");
    var pp = new ProcessStartInfo("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{spOut}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var ppp = Process.Start(pp)!; string spDur = ppp.StandardOutput.ReadToEnd().Trim(); ppp.WaitForExit();
    Check(double.TryParse(spDur, System.Globalization.CultureInfo.InvariantCulture, out var spd) && Math.Abs(spd - sproj.Duration) < 0.4, $"and the video is as long as the ramped item ({spDur} s, expected {sproj.Duration:0.00})");
    Run2("ffmpeg", "-y", "-loglevel", "error", "-ss", "0.2", "-i", outp, "-frames:v", "1", "-vf", "scale=480:-2", Path.Combine(dir, "anim_a.png"));
    Run2("ffmpeg", "-y", "-loglevel", "error", "-ss", "2.8", "-i", outp, "-frames:v", "1", "-vf", "scale=480:-2", Path.Combine(dir, "anim_b.png"));
}

else Console.WriteLine("skip  (no ffmpeg or sample clip here: the run test was not done)");

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} FAILED"); return fails;
