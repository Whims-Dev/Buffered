using System.Text.RegularExpressions;

namespace Buffered;

/// <summary>
/// Deletes the half-made files a save leaves behind when it is interrupted (the recorder is closed mid-save, the network drive drops,
/// a crash). They are hidden, named by the program, and only touched when they are more than an hour old, so a save in progress is safe.
/// </summary>
public static class TempSweep
{
    // ".2026-11-15_05-34-46.h264", ".2026-11-15_05-34-46.0.pcm", ".<anything>.partial.mp4", and ".share_<id>.mp4"
    private static readonly Regex Leftover = new(
        @"^\.(?:\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.(?:h264|\d+\.pcm)|.+\.partial\.mp4|share_[0-9a-f]{32}\.mp4)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly TimeSpan Age = TimeSpan.FromHours(1);

    /// <summary>Cleans the local work folder and the clips folder (and the folders one level inside it). Never throws.</summary>
    public static void Run(string clipsFolder)
    {
        var cutoff = DateTime.Now - Age;
        try
        {
            // The local work folder holds nothing but temporary files.
            string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "tmp");
            if (Directory.Exists(work))
                foreach (var f in Directory.EnumerateFiles(work)) TryDelete(f, cutoff, anyName: true);
        }
        catch { }

        try
        {
            if (!Directory.Exists(clipsFolder)) return;
            var places = new List<string> { clipsFolder };
            places.AddRange(Directory.EnumerateDirectories(clipsFolder));
            foreach (var place in places)
                foreach (var f in Directory.EnumerateFiles(place)) TryDelete(f, cutoff, anyName: false);
        }
        catch { /* a folder that can't be reached (a network drive that is off) is simply skipped */ }
    }

    private static void TryDelete(string file, DateTime cutoff, bool anyName)
    {
        try
        {
            if (!anyName && !Leftover.IsMatch(Path.GetFileName(file))) return;
            if (File.GetLastWriteTime(file) > cutoff) return;
            File.Delete(file);
        }
        catch { }
    }
}
