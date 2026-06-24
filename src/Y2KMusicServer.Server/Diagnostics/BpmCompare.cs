namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Compares a stored BPM against a freshly detected one, tolerating octave
/// (½ / 2×) folds — a detector frequently locks onto half or double the tempo,
/// which is musically the same tempo. The verdict is judged on the best-fold
/// error; the reported delta is always the direct (un-folded) difference, so a
/// genuine octave error in the stored value is visible as a large delta
/// annotated with the fold that matched. Shared by the live mix-point check and
/// the background library sweep.
/// </summary>
public static class BpmCompare
{
    public readonly record struct Result(string Verdict, string Fold, double DirectDelta, bool IsMismatch);

    public static Result Compare(double stored, double live)
    {
        double direct = live - stored;
        if (stored <= 0 || live <= 0)
            return new Result("?", "", direct, false);

        double effErr = Math.Abs(direct);
        string fold = "";

        double err2 = Math.Abs(live * 2 - stored);
        if (err2 < effErr) { effErr = err2; fold = " (½-tempo fold)"; }

        double errHalf = Math.Abs(live / 2 - stored);
        if (errHalf < effErr) { effErr = errHalf; fold = " (2×-tempo fold)"; }

        string verdict = effErr <= 1.5 ? "✓ match"
                       : effErr <= 5.0 ? "⚠ small diff"
                       : "✗ MISMATCH";
        return new Result(verdict, fold, direct, effErr > 5.0);
    }
}
