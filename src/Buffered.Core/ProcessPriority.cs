using System.Diagnostics;

namespace Buffered;

/// <summary>Lowering the priority of a helper program (ffmpeg and the like), so it never slows a game down. A process that has already ended is not an error.</summary>
public static class ProcessPriority
{
    public static void TrySet(Process process, ProcessPriorityClass priority)
    {
        try { process.PriorityClass = priority; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
