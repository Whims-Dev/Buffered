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
    var vp = new ItemProps().WithKey(KeyProp.PosX, 0, 0.2).WithKey(KeyProp.PosX, 3, 0.8).WithKey(KeyProp.Scale, 0, 0.6).WithKey(KeyProp.Scale, 3, 1.2).WithKey(KeyProp.Opacity, 0, 0.3).WithKey(KeyProp.Opacity, 3, 1).WithKey(KeyProp.Rotation, 0, 0).WithKey(KeyProp.Rotation, 3, 40);
    var av = V(0, 0, 3, clip, vp);
    var ap = new ItemProps().WithKey(KeyProp.Volume, 0, 0).WithKey(KeyProp.Volume, 3, 1.5);
    var asn = new TimelineItem(Guid.NewGuid(), ItemKind.Audio, "pcm", 0, 3, 0, ap, null, "s");
    var tp = new ItemProps(Text: "Hi", Rotation: 20).WithKey(KeyProp.Scale, 0, 0.5).WithKey(KeyProp.Scale, 2, 1.5).WithKey(KeyProp.PosY, 0, 0.2).WithKey(KeyProp.PosY, 2, 0.8);
    var at = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 2, 0.5, tp);
    var ap0 = Project.Empty((TrackKind.Text, "T"), (TrackKind.Video, "V"), (TrackKind.Audio, "A")).Add(1, av)!.Add(2, asn)!.Add(0, at)!;
    var animated = ExportGraph.Build(ap0, o, i => i.Kind == ItemKind.Audio ? pcm : null, i => png, s => null);
    Check(animated.FilterGraph.Contains("eval=frame") && animated.FilterGraph.Contains("sendcmd") && animated.FilterGraph.Contains("if(lt(t,") && animated.FilterGraph.Contains("rotate=a="), "the animated graph has expressions for position, zoom, opacity, volume and turn");
    Check(ExportGraph.ExtraFiltersFor(ap0).Contains("sendcmd") && !ExportGraph.ExtraFiltersFor(Project.Empty()).Any(), "it says which extra filter it needs");
    int code = Run2("ffmpeg", ExportGraph.Arguments(animated, o, outp, false).ToArray());
    Check(code == 0 && File.Exists(outp), "ffmpeg makes the animated video");

}

if (haveFfmpeg && File.Exists(clip))
{
    // cropping: the left and right thirds are cut off, what is left stays where it was
    string dir = Path.Combine(Path.GetTempPath(), "bf-export-test"); string outc = Path.Combine(dir, "crop.mp4");
    var cv = V(0, 0, 2, clip, new ItemProps(CropL: 0.3, CropR: 0.3, CropT: 0.1));
    var cp = Project.Empty((TrackKind.Video, "V")).Add(0, cv)!;
    var cplan = ExportGraph.Build(cp, o, _ => null, _ => null, _ => null);
    Check(cplan.FilterGraph.Contains("crop=w=") && ExportGraph.ExtraFiltersFor(cp).Contains("crop"), "the cropped picture has a crop filter and says it needs it");
    var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var a in ExportGraph.Arguments(cplan, o, outc, false)) psi.ArgumentList.Add(a);
    using var pc = Process.Start(psi)!; var cerr = pc.StandardError.ReadToEndAsync(); pc.StandardOutput.ReadToEnd(); pc.WaitForExit();
    if (pc.ExitCode != 0) Console.WriteLine(cerr.Result.Trim());
    Check(pc.ExitCode == 0 && File.Exists(outc), "ffmpeg makes the cropped video");
    var fr = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true }; foreach (var a in new[] { "-y", "-loglevel", "error", "-ss", "1", "-i", outc, "-frames:v", "1", "-vf", "scale=480:-2", Path.Combine(dir, "crop_a.png") }) fr.ArgumentList.Add(a); Process.Start(fr)!.WaitForExit();
    int Run3(string exe, params string[] args) { var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var a in args) psi.ArgumentList.Add(a); using var pr = Process.Start(psi)!; var err = pr.StandardError.ReadToEndAsync(); pr.StandardOutput.ReadToEnd(); pr.WaitForExit(); if (pr.ExitCode != 0) Console.WriteLine(err.Result.Trim()); return pr.ExitCode; }
    // a picture's fade-out really ends at the end of the item: sample the middle of the frame while it fades
    string white = Path.Combine(dir, "white.png");
    Run3("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=white:s=800x600,format=rgba", "-frames:v", "1", white);
    int Lum(string file, double at) { var q = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var a in new[] { "-loglevel", "error", "-ss", at.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-vf", "crop=1:1:640:360,format=gray", "-f", "rawvideo", "-" }) q.ArgumentList.Add(a); using var pr = Process.Start(q)!; var ms = new MemoryStream(); pr.StandardOutput.BaseStream.CopyTo(ms); pr.WaitForExit(); return ms.Length > 0 ? ms.ToArray()[0] : -1; }
    foreach (var (name, props) in new (string, ItemProps)[]
    {
        ("plain", new ItemProps(FadeOut: 2)),
        ("dimmer", new ItemProps(FadeOut: 2, Opacity: 0.8)),
        ("turned", new ItemProps(FadeOut: 2, Rotation: 10)),
        ("zooming", new ItemProps(FadeOut: 2).WithKey(KeyProp.Scale, 0, 1).WithKey(KeyProp.Scale, 5, 1.5)),
        ("moving", new ItemProps(FadeOut: 2).WithKey(KeyProp.PosX, 0, 0.5).WithKey(KeyProp.PosX, 5, 0.52)),
        ("fading in and out", new ItemProps(FadeIn: 1, FadeOut: 2)),
        ("fades longer than the item", new ItemProps(FadeIn: 3, FadeOut: 4)),
    })
    {
        var im = new TimelineItem(Guid.NewGuid(), ItemKind.Image, white, 0, 5, 2, props);
        var pf = Project.Empty((TrackKind.Video, "V")).Add(0, im)!;
        string fo = Path.Combine(dir, "fade_" + name.Replace(' ', '_') + ".mp4");
        var gp = ExportGraph.Build(pf, o with { Width = 1280, Height = 720 }, _ => null, _ => null, _ => (800, 600));
        int rc = Run3("ffmpeg", ExportGraph.Arguments(gp, o, fo, false).ToArray());
        int a = Lum(fo, 4.0), m1 = Lum(fo, 5.5), m2 = Lum(fo, 6.0), m3 = Lum(fo, 6.5), z = Lum(fo, 6.9);
        Console.WriteLine($"      {name}: 4.0s={a}  5.5s={m1}  6.0s={m2}  6.5s={m3}  6.9s={z}");
        Check(rc == 0 && a > m1 && m1 > m2 && m2 > m3 && m3 > z && z < 40, $"a picture's fade-out is smooth and reaches nothing at its end ({name})");
    }
    // a clip whose picture ends before its sound (the picture is shorter than the file) holds its last frame instead of going black
    string shortV = Path.Combine(dir, "short_picture.mp4"), shortOut = Path.Combine(dir, "short_out.mp4");
    Run3("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=white:s=640x360:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", shortV);
    var sv = new TimelineItem(Guid.NewGuid(), ItemKind.Video, shortV, 0, 3, 0, new ItemProps());
    var sp = Project.Empty((TrackKind.Video, "V")).Add(0, sv)!;
    var sg = ExportGraph.Build(sp, o with { Width = 1280, Height = 720 }, _ => null, _ => null, _ => null);
    int src = Run3("ffmpeg", ExportGraph.Arguments(sg, o, shortOut, false).ToArray());
    int early = Lum(shortOut, 1.0), late = Lum(shortOut, 2.7);
    Console.WriteLine($"      short picture: 1.0s={early}  2.7s={late}");
    Check(src == 0 && early > 200 && late > 200, "when a clip's picture ends before its sound, the last picture is held (no black at the end)");
    // effect lanes: a dip goes dark in the middle of the cut, a shake moves the picture only while it lasts
    string chart = Path.Combine(dir, "chart.mp4"), plainOut = Path.Combine(dir, "fx_plain.mp4"), dipOut = Path.Combine(dir, "fx_dip.mp4"), whiteOut = Path.Combine(dir, "fx_white.mp4"), shakeOut = Path.Combine(dir, "fx_shake.mp4");
    Run3("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", chart);
    var cv0 = new TimelineItem(Guid.NewGuid(), ItemKind.Video, chart, 0, 4, 0, new ItemProps());
    var baseP = Project.Empty((TrackKind.Effect, "Effects"), (TrackKind.Video, "V")).Add(1, cv0)!;
    byte[] Frame(string file, double at) { var q = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var a in new[] { "-loglevel", "error", "-ss", at.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-vf", "scale=160:90,format=gray", "-f", "rawvideo", "-" }) q.ArgumentList.Add(a); using var pr = Process.Start(q)!; var ms = new MemoryStream(); pr.StandardOutput.BaseStream.CopyTo(ms); pr.WaitForExit(); return ms.ToArray(); }
    int RunFx(Project pj, string outFile) { var g = ExportGraph.Build(pj, o with { Width = 640, Height = 360 }, _ => null, _ => null, _ => null); return Run3("ffmpeg", ExportGraph.Arguments(g, o, outFile, false).ToArray()); }
    TimelineItem Fx(string type, double start, double len) => new(Guid.NewGuid(), ItemKind.Effect, "", 0, len, start, new ItemProps(Effect: type));
    int r0 = RunFx(baseP, plainOut);
    int r1 = RunFx(baseP.Add(0, Fx(Effects.DipBlack, 1.5, 1.0))!, dipOut);
    int r2 = RunFx(baseP.Add(0, Fx(Effects.DipWhite, 1.5, 1.0))!, whiteOut);
    int r3 = RunFx(baseP.Add(0, Fx(Effects.Shake, 1.0, 0.6))!, shakeOut);
    double Mean(byte[] b) => b.Length == 0 ? -1 : b.Average(x => (double)x);
    Console.WriteLine($"      dip black: 1.0s={Mean(Frame(dipOut, 1.0)):0}  2.0s (middle)={Mean(Frame(dipOut, 2.0)):0}  3.0s={Mean(Frame(dipOut, 3.0)):0}  plain 2.0s={Mean(Frame(plainOut, 2.0)):0}");
    Check(r0 == 0 && r1 == 0 && Mean(Frame(dipOut, 2.0)) < 12 && Mean(Frame(dipOut, 1.0)) > 40 && Mean(Frame(dipOut, 3.0)) > 40 && Mean(Frame(dipOut, 1.75)) < Mean(Frame(plainOut, 1.75)) - 10, "a transition on an effect lane goes dark in the middle of the cut and is clear outside it");
    Check(r2 == 0 && Mean(Frame(whiteOut, 2.0)) > 240 && Mean(Frame(whiteOut, 1.0)) < 200, "a dip to white flashes white in the middle");
    double Diff(double at) { var x = Frame(plainOut, at); var y = Frame(shakeOut, at); return x.Length == 0 || x.Length != y.Length ? 999 : x.Zip(y, (m, n) => Math.Abs(m - n)).Average(); }
    Console.WriteLine($"      shake difference from the plain picture: 0.5s={Diff(0.5):0.0}  1.1s={Diff(1.1):0.0}  1.2s={Diff(1.2):0.0}  3.0s={Diff(3.0):0.0}");
    Check(r3 == 0 && Diff(1.1) > 8 && Diff(0.5) < 2 && Diff(3.0) < 2, "a shake moves the picture only while it lasts");
    Check(ExportGraph.ExtraFiltersFor(baseP.Add(0, Fx(Effects.Shake, 1.0, 0.6))!).Contains("split"), "a shake says it needs the split filter");
}

else Console.WriteLine("skip  (no ffmpeg or sample clip here: the run test was not done)");

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} FAILED"); return fails;
