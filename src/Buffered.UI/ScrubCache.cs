using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Buffered.UI;

/// <summary>
/// Smooth-scrubbing copies of long clips, made ahead of time and kept between sessions. A clip straight from the recorder has a keyframe only every couple of seconds, so
/// jumping around in it makes the player decode its way to each spot; a copy with a keyframe every few frames does not. Making one takes a while, so it is started as early
/// as there is a reason to (the mouse resting on a clip, the newest clips when the window opens) and kept, so opening the clip later is instant. Copies are named after the
/// clip's path, size and modified time, so an edited clip never meets an old copy, and the oldest are removed when they add up to more than a few gigabytes.
/// </summary>
internal static class ScrubCache
{
    private const long MaxBytes = 3L * 1024 * 1024 * 1024;
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "scrub");
    private static readonly ConcurrentDictionary<string, Task<string?>> Jobs = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string KeyOf(string clip)
    {
        var fi = new FileInfo(clip);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{clip}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..20];
    }

    private static string CopyPath(string key) => Path.Combine(Dir, key + ".mp4");
    private static string FinePath(string key) => Path.Combine(Dir, key + ".fine");   // a note that this clip needs no copy

    /// <summary>How much disk the kept copies take, in megabytes.</summary>
    public static long DiskMb()
    {
        try { return !Directory.Exists(Dir) ? 0 : new DirectoryInfo(Dir).GetFiles("*.mp4").Sum(f => f.Length) / 1048576; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The copy of this clip if one has been made, or null. Opening a clip that has one needs no waiting at all.</summary>
    public static string? Existing(string clip)
    {
        try
        {
            string path = CopyPath(KeyOf(clip));
            if (!File.Exists(path)) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // used just now: last in line to be removed
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when the clip is known to seek well as it is.</summary>
    public static bool KnownFine(string clip)
    {
        try { return File.Exists(FinePath(KeyOf(clip))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Makes the copy if the clip needs one (once, even if asked for from several places) and returns its path, or null if the clip needs none or it could not be made.
    /// <paramref name="gap"/> is the clip's longest stretch between keyframes if the caller already knows it.
    /// </summary>
    public static Task<string?> EnsureAsync(string clip, double? gap = null, bool lowImpact = false)
    {
        string key;
        try { key = KeyOf(clip); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult<string?>(null); }
        return Jobs.GetOrAdd(key, _ => Build(clip, key, gap, lowImpact));
    }

    private static async Task<string?> Build(string clip, string key, double? knownGap, bool lowImpact)
    {
        try
        {
            string path = CopyPath(key);
            if (File.Exists(path)) return path;
            if (File.Exists(FinePath(key))) return null;
            await OneAtATime.WaitAsync();
            try
            {
                if (File.Exists(path)) return path;
                double gap = knownGap ?? await Media.MaxKeyframeGapAsync(clip);
                Directory.CreateDirectory(Dir);
                if (gap <= 0.75) { File.WriteAllBytes(FinePath(key), Array.Empty<byte>()); return null; }
                string? made = await Media.MakeScrubCopyAsync(clip, lowImpact);
                if (made is null) return null;
                try { File.Move(made, path, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { try { File.Delete(made); } catch { } return null; }
                Trim();
                return path;
            }
            finally { OneAtATime.Release(); }
        }
        finally { Jobs.TryRemove(key, out _); }
    }

    /// <summary>Starts making copies, one after another, for clips that are worth it (long ones). Does nothing for those already done.</summary>
    public static void PrepareInBackground(IEnumerable<string> clips)
    {
        _ = Task.Run(async () =>
        {
            foreach (var clip in clips)
            {
                try { if (File.Exists(clip) && Existing(clip) is null && !KnownFine(clip)) await EnsureAsync(clip, null, lowImpact: true); }   // nobody is waiting for it: the lowest priority, one thread, so a game is never disturbed
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>Removes the oldest copies until the rest take up no more than the limit.</summary>
    private static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= MaxBytes) break;
                try { total -= f.Length; f.Delete(); } catch (IOException) { }
            }
            // notes about clips that are fine as they are cost nothing, but do not let them pile up for ever
            var notes = new DirectoryInfo(Dir).GetFiles("*.fine").OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var n in notes.Take(Math.Max(0, notes.Count - 5000))) { try { n.Delete(); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
