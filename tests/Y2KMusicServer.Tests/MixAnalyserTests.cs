using Xunit;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Tests;

public class MixAnalyserTests
{
    private const double WinSec = 0.2;

    // Flat energy envelope of a given duration (RMS ~1.0 everywhere).
    private static float[] FlatEnergy(double seconds)
    {
        int n = (int)(seconds / WinSec);
        var e = new float[n];
        for (int i = 0; i < n; i++) e[i] = 1.0f;
        return e;
    }

    private static bool OnGrid(double t, double bpm, double phase, double tol = 0.02)
    {
        double beat = 60.0 / bpm;
        double ph = ((phase % beat) + beat) % beat;
        double r = ((t - ph) % beat + beat) % beat;
        r = Math.Min(r, beat - r);
        return r <= tol;
    }

    [Fact]
    public void OutAndIn_snap_to_their_beat_grids()
    {
        double durA = 60, durB = 60, bpm = 120, phaseA = 0.13, phaseB = 0.27;
        var mp = MixAnalyser.PlanFromEnergy(
            FlatEnergy(durA), durA, bpm, phaseA,
            FlatEnergy(durB), durB, bpm, phaseB,
            fadeDuration: 4.0);

        Assert.True(mp.IsValid);
        Assert.True(OnGrid(mp.OutPoint, bpm, phaseA), $"out {mp.OutPoint} off A grid");
        Assert.True(OnGrid(mp.InPoint, bpm, phaseB), $"in {mp.InPoint} off B grid");
    }

    [Fact]
    public void Matching_tempo_with_equal_phase_is_beat_aligned()
    {
        double dur = 60, bpm = 128, phase = 0.2;
        var mp = MixAnalyser.PlanFromEnergy(
            FlatEnergy(dur), dur, bpm, phase,
            FlatEnergy(dur), dur, bpm, phase,
            fadeDuration: 4.0);

        Assert.True(mp.BeatAligned);
        Assert.True(mp.PairScore > 0.8, $"score unexpectedly low: {mp.PairScore}");
    }

    [Fact]
    public void Out_point_stays_near_the_energy_derived_point()
    {
        // With matching tempo every beat aligns equally; the tie-break must keep
        // the out-point near where the energy analysis put it (late in the track),
        // not drag it many beats earlier.
        double dur = 60, bpm = 120;
        var mp = MixAnalyser.PlanFromEnergy(
            FlatEnergy(dur), dur, bpm, 0.0,
            FlatEnergy(dur), dur, bpm, 0.0,
            fadeDuration: 4.0);

        Assert.True(mp.OutPoint > dur - 8.0, $"out-point dragged too early: {mp.OutPoint}");
    }

    [Fact]
    public void No_bpm_means_no_snapping_and_not_aligned()
    {
        double dur = 60;
        var mp = MixAnalyser.PlanFromEnergy(
            FlatEnergy(dur), dur, 0, 0,
            FlatEnergy(dur), dur, 0, 0,
            fadeDuration: 3.0);

        Assert.False(mp.BeatAligned);
        Assert.True(mp.IsValid);
    }
}
