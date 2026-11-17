using System.IO;
using System.Text.RegularExpressions;

namespace Buffered.UI;

/// <summary>
/// Removes files an older version left behind. Each release lists its own files in files.txt; anything of the app's own kinds
/// (libraries, and the ffmpeg files in the tools folder) that is not on that list belongs to an older version and is deleted.
/// Everything else in the folder, and anything outside it, is never touched.
/// </summary>
internal static class InstallCleaner
{
    private const char Sep = '\\';

    /// <summary>Returns how many files were removed and how many bytes that freed. Does nothing without a files.txt (a development build).</summary>
    public static (int Files, long Bytes) Clean(string dir, IEnumerable<string?>? protectedFolders = null)
    {
        // The folders clips are saved in are never touched, wherever they are (even if someone put them inside Buffered's own folder).
        var protect = (protectedFolders ?? Enumerable.Empty<string?>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => { try { return Path.GetFullPath(p!).TrimEnd(Sep) + Sep; } catch { return null; } })
            .Where(p => p is not null).Select(p => p!).ToList();
        string manifest = Path.Combine(dir, "files.txt");
        if (!File.Exists(manifest)) return (0, 0);
        var keep = new HashSet<string>(
            File.ReadAllLines(manifest).Select(l => l.Trim().Replace('/', Sep)).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase) { "files.txt" };
        if (keep.Count < 20) return (0, 0);   // a list this short isn't a real one: don't act on it

        int files = 0; long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            string rel = Path.GetRelativePath(dir, f);
            if (keep.Contains(rel) || !IsOurs(rel)) continue;
            string full = Path.GetFullPath(f);
            if (protect.Any(p => full.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try { long len = new FileInfo(f).Length; File.Delete(f); files++; bytes += len; }
            catch { /* in use, or not allowed: leave it */ }
        }
        // folders the clean-up emptied
        foreach (var sub in new[] { "tools", "runtimes" })
        {
            string p = Path.Combine(dir, sub);
            if (!Directory.Exists(p)) continue;
            foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        return (files, bytes);
    }

    /// <summary>Only the app's own kinds of files: libraries and runtime files in the folder itself, libraries and programs in tools, anything in runtimes.</summary>
    internal static bool IsOurs(string rel)
    {
        string lower = rel.ToLowerInvariant();
        if (lower.StartsWith("runtimes" + Sep)) return lower.EndsWith(".dll");   // the native libraries that come with the app, nothing else
        if (lower.StartsWith("tools" + Sep)) return lower.EndsWith(".dll") || lower.EndsWith(".exe");
        if (lower.Contains(Sep)) return false;    // any other folder is not ours
        // In the folder itself, only the "name.dll.old123" copies left by swapping files in by hand. Other libraries and programs here are left
        // alone: someone may have put their own ffmpeg (with its DLLs) next to BufferedUI.exe, as the app's own message suggests.
        return Regex.IsMatch(lower, @"\.(dll|pdb|exe)\.old\w*$");
    }
}
