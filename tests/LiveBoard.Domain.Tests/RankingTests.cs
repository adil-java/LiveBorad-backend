using LiveBoard.Domain;

namespace LiveBoard.Domain.Tests;

public class RankingTests
{
    [Fact]
    public void Between_EmptyList_ReturnsStep() => Assert.Equal(Ranking.Step, Ranking.Between(null, null));

    [Fact]
    public void Between_AtEnd_AddsStep() => Assert.Equal(3072, Ranking.Between(2048, null));

    [Fact]
    public void Between_AtStart_SubtractsStep() => Assert.Equal(0, Ranking.Between(null, 1024));

    [Fact]
    public void Between_TwoNeighbours_ReturnsMidpoint() => Assert.Equal(1536, Ranking.Between(1024, 2048));

    [Theory]
    [InlineData(2048, 1024)]
    [InlineData(1024, 1024)]
    public void Between_NeighboursOutOfOrder_Throws(double before, double after) =>
        Assert.Throws<DomainValidationException>(() => Ranking.Between(before, after));

    [Fact]
    public void RepeatedInsertsIntoSameGap_EventuallyRequireRebalance()
    {
        double before = 1024, after = 2048;
        var inserts = 0;
        while (!Ranking.NeedsRebalance(before, after))
        {
            after = Ranking.Between(before, after);
            inserts++;
        }

        // Plenty of headroom before a rebalance is needed, and it is always detected before precision runs out.
        Assert.InRange(inserts, 30, 60);
        Assert.True(before < after);
    }

    [Fact]
    public void Spread_ReturnsEvenlySpacedPositions() =>
        Assert.Equal([1024, 2048, 3072], Ranking.Spread(3));
}
