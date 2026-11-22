using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

/// <summary>
/// Card writes are field-scoped, conditional updates rather than whole-document replaces: an edit only
/// touches content fields, a move only touches placement fields, and a column rebalance only touches
/// positions. That way a rebalance can never be overwritten by a concurrent edit carrying a stale position.
/// </summary>
public sealed class CardRepository(MongoContext context)
{
    private readonly IMongoCollection<Card> _cards = context.Cards;
    private static FilterDefinitionBuilder<Card> Filter => Builders<Card>.Filter;
    private static UpdateDefinitionBuilder<Card> Update => Builders<Card>.Update;

    public async Task<Card?> GetAsync(Guid boardId, Guid cardId, CancellationToken ct) =>
        await _cards.Find(c => c.Id == cardId && c.BoardId == boardId).FirstOrDefaultAsync(ct);

    public Task<List<Card>> ListByBoardAsync(Guid boardId, CancellationToken ct) =>
        _cards.Find(c => c.BoardId == boardId).SortBy(c => c.Position).ThenBy(c => c.Id).ToListAsync(ct);

    public Task<long> CountInColumnAsync(Guid boardId, Guid columnId, CancellationToken ct) =>
        _cards.CountDocumentsAsync(c => c.BoardId == boardId && c.ColumnId == columnId, cancellationToken: ct);

    public async Task<double?> MaxPositionAsync(Guid boardId, Guid columnId, Guid? excludingCardId, CancellationToken ct)
    {
        var filter = Filter.Eq(c => c.BoardId, boardId) & Filter.Eq(c => c.ColumnId, columnId);
        if (excludingCardId is Guid excluded)
            filter &= Filter.Ne(c => c.Id, excluded);

        var top = await _cards.Find(filter).SortByDescending(c => c.Position).Limit(1).FirstOrDefaultAsync(ct);
        return top?.Position;
    }

    public Task InsertAsync(Card card, CancellationToken ct) => _cards.InsertOneAsync(card, cancellationToken: ct);

    /// <summary>Persists title/description if the stored version still equals <paramref name="expectedVersion"/>.</summary>
    public Task<bool> SaveContentAsync(Card card, int expectedVersion, CancellationToken ct) =>
        SaveIfVersionAsync(card, expectedVersion, Update
            .Set(c => c.Title, card.Title)
            .Set(c => c.Description, card.Description), ct);

    /// <summary>Persists column/position if the stored version still equals <paramref name="expectedVersion"/>.</summary>
    public Task<bool> SaveMoveAsync(Card card, int expectedVersion, CancellationToken ct) =>
        SaveIfVersionAsync(card, expectedVersion, Update
            .Set(c => c.ColumnId, card.ColumnId)
            .Set(c => c.Position, card.Position), ct);

    /// <summary>
    /// Respaces every card in a column to Step, 2*Step, ... keeping their order. Positions are not
    /// user-visible content, so this does not bump versions (it must not cause edit conflicts).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, double>> RebalanceColumnAsync(Guid boardId, Guid columnId, CancellationToken ct)
    {
        var cards = await _cards.Find(c => c.BoardId == boardId && c.ColumnId == columnId)
            .SortBy(c => c.Position).ThenBy(c => c.Id)
            .Project(c => c.Id)
            .ToListAsync(ct);

        var positions = cards.Zip(Ranking.Spread(cards.Count)).ToDictionary(p => p.First, p => p.Second);
        if (positions.Count == 0)
            return positions;

        var writes = positions.Select(p => new UpdateOneModel<Card>(
            Filter.Eq(c => c.Id, p.Key), Update.Set(c => c.Position, p.Value)));
        await _cards.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
        return positions;
    }

    public async Task<bool> DeleteAsync(Guid boardId, Guid cardId, CancellationToken ct) =>
        (await _cards.DeleteOneAsync(c => c.Id == cardId && c.BoardId == boardId, ct)).DeletedCount == 1;

    public Task DeleteByBoardAsync(Guid boardId, CancellationToken ct) =>
        _cards.DeleteManyAsync(c => c.BoardId == boardId, ct);

    private async Task<bool> SaveIfVersionAsync(Card card, int expectedVersion, UpdateDefinition<Card> changes, CancellationToken ct)
    {
        var update = Update.Combine(changes,
            Update.Set(c => c.Version, card.Version),
            Update.Set(c => c.UpdatedBy, card.UpdatedBy),
            Update.Set(c => c.UpdatedAt, card.UpdatedAt));

        var result = await _cards.UpdateOneAsync(
            c => c.Id == card.Id && c.BoardId == card.BoardId && c.Version == expectedVersion, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }
}
