namespace Buffered.UI;

/// <summary>
/// Volume as people think of it: a percentage, 100% being the sound as it was recorded, 0% silent, 200% twice as loud (amplitude).
/// Settings and the audio tools underneath keep using decibels; these turn one into the other at the sliders.
/// </summary>
internal static class Vol
{
    /// <summary>What 0% is stored as: quiet enough to be silence.</summary>
    public const double SilentDb = -80;

    public static double PercentToDb(double percent) => percent <= 0.5 ? SilentDb : Math.Round(20 * Math.Log10(percent / 100.0), 2);

    public static double DbToPercent(double db) => db <= SilentDb + 1 ? 0 : Math.Clamp(Math.Round(100 * Math.Pow(10, db / 20.0)), 0, 200);

    public static string Text(double percent) => $"{Math.Round(percent):0}%";
}
