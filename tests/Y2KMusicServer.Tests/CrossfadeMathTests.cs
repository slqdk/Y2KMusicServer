using Xunit;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Tests;

public class CrossfadeMathTests
{
    [Fact]
    public void Fade_fits_when_room_is_ample()
    {
        // trigger at 200s, 6s fade, track ends at 240s → plenty of room.
        Assert.Equal(6.0, CrossfadeMath.EffectiveFadeSec(200, 6, 240), 3);
        Assert.False(CrossfadeMath.WasShortened(200, 6, 240));
    }

    [Fact]
    public void Fade_is_shortened_to_land_exactly_at_eof()
    {
        // trigger at 237s, 6s fade, track ends at 240s → only 3s of room.
        Assert.Equal(3.0, CrossfadeMath.EffectiveFadeSec(237, 6, 240), 3);
        Assert.True(CrossfadeMath.WasShortened(237, 6, 240));
    }

    [Fact]
    public void Fade_never_collapses_below_floor()
    {
        // trigger past the end → clamp to the minimum, not zero/negative.
        Assert.Equal(CrossfadeMath.MinFadeSec, CrossfadeMath.EffectiveFadeSec(241, 6, 240), 3);
    }

    [Fact]
    public void Ramp_reaches_endpoints_by_full_progress()
    {
        const float start = 0.8f;
        const float target = 0.8f;

        // pos = 0 → A at start, B silent.
        Assert.Equal(start, CrossfadeMath.VolA(start, 0.0), 3);
        Assert.Equal(0f, CrossfadeMath.VolB(target, 0.0), 3);

        // pos = 1 → A silent, B at target.
        Assert.Equal(0f, CrossfadeMath.VolA(start, 1.0), 3);
        Assert.Equal(target, CrossfadeMath.VolB(target, 1.0), 3);
    }

    [Fact]
    public void Step_sums_to_one_over_the_fade()
    {
        double tickMs = 50;
        double fadeSec = 4;
        double step = CrossfadeMath.StepPerTick(tickMs, fadeSec);

        // Number of ticks across the fade, times the step, should reach ~1.0.
        int ticks = (int)(fadeSec * 1000 / tickMs);
        double pos = step * ticks;
        Assert.Equal(1.0, pos, 3);
    }

    [Fact]
    public void Ramp_volumes_are_clamped()
    {
        // Negative/overshoot progress must not push volumes out of range.
        Assert.Equal(0f, CrossfadeMath.VolB(0.8f, -0.5), 3);
        Assert.Equal(0.8f, CrossfadeMath.VolB(0.8f, 2.0), 3);
        Assert.Equal(0f, CrossfadeMath.VolA(0.8f, 2.0), 3);
    }
}
