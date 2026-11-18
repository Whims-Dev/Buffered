namespace Buffered.UI;

/// <summary>What the editor knows about the picture on this tick.</summary>
/// <param name="Now">A steady clock, in seconds.</param>
/// <param name="Playing">The clip is meant to be playing.</param>
/// <param name="Reported">Where the video player says it is (it reports in small jumps and can be stale just after a jump).</param>
/// <param name="Duration">The length of the clip.</param>
/// <param name="Limit">How far the playhead may go: the end of the clip, or the trim's end while a trim is being previewed.</param>
/// <param name="Stalled">The picture has stopped moving while it should be playing (the player is still decoding its way to a spot).</param>
/// <param name="Current">Where the playhead is drawn now.</param>
internal readonly record struct PlayheadInput(double Now, bool Playing, double Reported, double Duration, double Limit, bool Stalled, double Current);

/// <summary>What to do with the playhead. <see cref="Position"/> is where to draw it (null: leave it); <see cref="Follow"/> is true on an ordinary tick of
/// playing, when the sound should be kept with the picture and the trim end checked.</summary>
internal readonly record struct PlayheadResult(double? Position, bool Follow);

/// <summary>
/// Decides where the playhead is drawn. The video player's position is not good enough on its own: it reports in small jumps, can be stale for a moment after a jump,
/// reads zero after running off its end, and does not move while it is decoding its way to a new spot. This keeps one answer to "where is the playhead" from those
/// facts, and nothing else: it knows nothing about the screen or the player, so it can be tried with made-up inputs.
/// </summary>
internal sealed class PlayheadModel
{
    private const double HoldMs = 0.6;
    private double _holdTarget, _holdFrom, _holdUntil = double.NegativeInfinity;
    private double _lastReported = double.NaN, _lastReportedAt;
    private bool _settling;
    private double _settleAt;

    /// <summary>Where the playhead was last drawn by this model.</summary>
    public double Shown { get; private set; }

    /// <summary>After a jump in a long clip: the picture is paused at the spot while the player gets there, and the playhead stays exactly there too.</summary>
    public bool Settling => _settling;

    /// <summary>The clip has run to its end: the playhead stays at the end until the user moves it.</summary>
    public bool AtEnd { get; private set; }

    /// <summary>Starts over for a newly opened clip.</summary>
    public void Reset()
    {
        Shown = 0; AtEnd = false; _settling = false; _holdUntil = double.NegativeInfinity; _lastReported = double.NaN;
    }

    /// <summary>The player has run off the end of the clip.</summary>
    public void Ended(double duration) { AtEnd = true; Shown = duration; }

    /// <summary>The user is moving the playhead, or has asked to play from the end again.</summary>
    public void LeaveEnd() => AtEnd = false;

    /// <summary>The playhead was put somewhere on purpose (a click, an arrow key): show it there at once.</summary>
    public void Put(double seconds) { AtEnd = false; Shown = seconds; }

    /// <summary>
    /// For a moment after pausing, starting or dropping the playhead, ignore a stale position from the player and keep the playhead at the given spot
    /// (then moving on by the clock, if playing).
    /// </summary>
    public void Hold(double at, double now, double seconds = HoldMs)
    {
        _holdTarget = at; _holdFrom = now; _holdUntil = now + seconds;
    }

    /// <summary>The picture is paused at a spot while the player decodes its way there. The playhead stays at the spot until <see cref="EndSettle"/>.</summary>
    public void BeginSettle(double at) { _settling = true; _settleAt = at; }

    /// <summary>The wait is over: the first position the picture reports counts as new, so the playhead is not carried ahead by the time spent waiting.</summary>
    public void EndSettle(double now)
    {
        _settling = false; _lastReported = double.NaN; _lastReportedAt = now;
        _holdUntil = double.NegativeInfinity;   // the picture only starts now: the playhead must not have been running on the clock since the drop
    }

    public void CancelSettle() => _settling = false;

    /// <summary>One tick of the clock.</summary>
    public PlayheadResult Update(in PlayheadInput t)
    {
        if (AtEnd && !t.Playing) return new(Math.Abs(t.Current - t.Duration) > 1e-4 ? t.Duration : null, false);

        if (_settling)
        {
            Shown = _settleAt; _lastReported = double.NaN; _lastReportedAt = t.Now;
            return new(Math.Abs(t.Current - _settleAt) > 1e-4 ? _settleAt : null, false);
        }

        double pos = t.Reported;
        // After running off its end, the player can report a position of zero for a moment before it says it has ended. Do not take that for a jump to the start.
        if (t.Playing && pos < 0.05 && Shown > t.Duration - 0.6 && Shown > 1) return new(null, false);

        // The player only reports its position in small jumps; between them the playhead is moved on by the clock, so it glides.
        if (double.IsNaN(_lastReported) || Math.Abs(pos - _lastReported) > 1e-6) { _lastReported = pos; _lastReportedAt = t.Now; }
        else pos = Math.Min(pos + Math.Min(t.Now - _lastReportedAt, 0.25), t.Duration);

        // While paused, the playhead stays exactly where it was put. The player's own position means nothing then: it can read zero after a jump to the very end,
        // or settle a frame away, and the playhead must not follow that.
        if (!t.Playing) { Shown = t.Current; return new(null, false); }

        bool holding = t.Now < _holdUntil;
        pos = Math.Min(pos, t.Limit);
        if (!holding)
        {
            if (pos < Shown && Shown - pos < 0.12) pos = Shown;   // the player reports a little behind the clock: don't step back
            if (t.Stalled) pos = Shown;                            // while the picture is catching up the playhead stands where it was put
        }
        Shown = pos;

        // Just after a drop the player may still report the old position. Until it catches up, carry the playhead on from the drop point
        // by the clock (while playing), so it doesn't sit still waiting for the video.
        if (holding)
        {
            double expected = Math.Min(_holdTarget + (t.Now - _holdFrom), t.Limit);
            if (t.Stalled) expected = Shown;
            // It follows the clock for the first moments (the player's first reports are stale), and after that the player's own position if that is close.
            if (t.Now - _holdFrom < 0.25 || Math.Abs(t.Reported - expected) > 0.4)
            {
                Shown = expected;
                return new(expected, false);
            }
        }
        return new(pos, true);
    }
}
