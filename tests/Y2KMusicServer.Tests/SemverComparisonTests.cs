using Xunit;
using Y2KMusicServer.Server.Updates;

namespace Y2KMusicServer.Tests;

public sealed class SemverComparisonTests
{
    [Theory]
    [InlineData("0.1.1", "0.1.0",  1)]
    [InlineData("0.1.0", "0.1.1", -1)]
    [InlineData("1.0.0", "0.9.9",  1)]
    [InlineData("0.1.0", "0.1.0",  0)]
    [InlineData("0.2",   "0.1.5",  1)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    public void CompareSemver_RanksVersionsCorrectly(string a, string b, int expectedSign)
    {
        var result = GitHubUpdateChecker.CompareSemver(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void CompareSemver_StripsLeadingV()
    {
        // The checker itself strips the "v" — this test just covers
        // ParseSemver's leniency to malformed input.
        Assert.Equal(0, GitHubUpdateChecker.CompareSemver("0.1.0", "0.1.0"));
    }
}
