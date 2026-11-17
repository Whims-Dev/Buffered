using System.Diagnostics;
using System.IO;
using System.Text;

namespace Buffered.UI;

/// <summary>
/// A short memory of what the editor has just been doing: every jump, drag, play and pause, and the state of the playhead and the sound a few times a second.
/// It is always on and costs almost nothing (it keeps only the last few minutes, in memory). When something goes wrong, press Ctrl+Shift+L in the editor and the
/// memory is written to a file, so the exact moment can be read afterwards instead of being guessed at.
/// </summary>
internal sealed class EditorTrace
{
    private const int Capacity = 8000;
    private readonly string[] _lines = new string[Capacity];
    private int _next, _count;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _gate = new();

    /// <summary>Seconds since the trace began.</summary>
    private double Elapsed => Stopwatch.GetElapsedTime(_start).TotalSeconds;

    public void Add(string text)
    {
        string line = $"{Elapsed,9:0.000}  {text}";
        lock (_gate) { _lines[_next] = line; _next = (_next + 1) % Capacity; if (_count < Capacity) _count++; }
    }

    /// <summary>Writes what is remembered, oldest first, with a few lines saying what was open. Returns the file's path, or null if it could not be written.</summary>
    public string? Dump(string header)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Buffered editor trace");
            sb.AppendLine(header);
            sb.AppendLine("columns: seconds since the editor opened, then what happened or the state at that moment");
            sb.AppendLine(new string('-', 100));
            lock (_gate)
            {
                int first = _count < Capacity ? 0 : _next;
                for (int i = 0; i < _count; i++) sb.AppendLine(_lines[(first + i) % Capacity]);
            }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"editor-trace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
