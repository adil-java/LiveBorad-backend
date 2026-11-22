namespace LiveBoard.Domain;

/// <summary>
/// Fractional ranking: an item's position is a double, and inserting between two neighbours
/// takes the midpoint. Moving a card therefore writes exactly one document instead of
/// renumbering the whole column, which keeps real-time broadcasts small and avoids contention.
/// </summary>
public static class Ranking
{
    public const double Step = 1024d;

    /// <summary>
    /// Smallest gap allowed between neighbours before the column must be respaced.
    /// Starting from <see cref="Step"/>, doubles allow ~40 halvings before this is reached.
    /// </summary>
    public const double MinGap = 1e-9;

    /// <param name="before">Position of the item that will sit directly above, or null for the top.</param>
    /// <param name="after">Position of the item that will sit directly below, or null for the bottom.</param>
    public static double Between(double? before, double? after) => (before, after) switch
    {
        (null, null) => Step,
        (double b, null) => b + Step,
        (null, double a) => a - Step,
        (double b, double a) when b >= a =>
            throw new DomainValidationException("The item above must have a lower position than the item below."),
        (double b, double a) => b + (a - b) / 2,
    };

    public static bool NeedsRebalance(double? before, double? after) =>
        before is double b && after is double a && a - b < MinGap;

    /// <summary>Evenly spaced positions for <paramref name="count"/> items: Step, 2*Step, ...</summary>
    public static IReadOnlyList<double> Spread(int count) =>
        Enumerable.Range(1, count).Select(i => i * Step).ToArray();
}
