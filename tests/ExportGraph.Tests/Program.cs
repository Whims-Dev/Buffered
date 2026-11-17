using System.Diagnostics;
using Buffered.UI;
int fails = 0; void Check(bool ok, string what) { Console.WriteLine((ok ? "pass  " : "FAIL  ") + what); if (!ok) fails++; }

var o = new ExportOptions(1280, 720, 30, 25, 21, false);
TimelineItem V(double start, double srcIn, double srcOut, string path, ItemProps? pr = null) => new(Guid.NewGuid(), ItemKind.Video, path, srcIn, srcOut, start, pr ?? new ItemProps());
var p0 = Project.Empty((TrackKind.Text, "T"), (TrackKind.Video, "V"), (TrackKind.Audio, "A"));
string clip = @"E:\Cloud Upload\Clips\VALORANT\VALORANT_2026-11-15_03-58-03.mp4";
var v1 = V(0, 0, 3, clip, new ItemProps(FadeIn: 0.5, Opacity: 0.9)); var v2 = V(4, 3, 7, clip);
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
else Console.WriteLine("skip  (no ffmpeg or sample clip here: the run test was not done)");

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} FAILED"); return fails;
