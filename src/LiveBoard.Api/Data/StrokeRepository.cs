using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class StrokeRepository(MongoContext context)
{
    private readonly IMongoCollection<Stroke> _strokes = context.Strokes;

    /// <summary>Returns false if a stroke with this ID already exists (e.g. the client retried after a timeout).</summary>
    public async Task<bool> TryInsertAsync(Stroke stroke, CancellationToken ct)
    {
        try
        {
            await _strokes.InsertOneAsync(stroke, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<Stroke?> GetAsync(Guid strokeId, CancellationToken ct) =>
        await _strokes.Find(s => s.Id == strokeId).FirstOrDefaultAsync(ct);

    /// <summary>In drawing order, oldest first, so later strokes paint over earlier ones.</summary>
    public Task<List<Stroke>> ListByBoardAsync(Guid boardId, CancellationToken ct) =>
        _strokes.Find(s => s.BoardId == boardId).SortBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);

    public Task<long> CountAsync(Guid boardId, CancellationToken ct) =>
        _strokes.CountDocumentsAsync(s => s.BoardId == boardId, cancellationToken: ct);

    /// <summary>Deletes those of <paramref name="strokeIds"/> that belong to the board and returns the IDs actually deleted.</summary>
    public async Task<List<Guid>> DeleteManyAsync(Guid boardId, IReadOnlyCollection<Guid> strokeIds, CancellationToken ct)
    {
        var filter = Builders<Stroke>.Filter.Eq(s => s.BoardId, boardId) & Builders<Stroke>.Filter.In(s => s.Id, strokeIds);
        var existing = await _strokes.Find(filter).Project(s => s.Id).ToListAsync(ct);
        if (existing.Count > 0)
            await _strokes.DeleteManyAsync(Builders<Stroke>.Filter.Eq(s => s.BoardId, boardId) & Builders<Stroke>.Filter.In(s => s.Id, existing), ct);
        return existing;
    }

    public async Task<long> DeleteByBoardAsync(Guid boardId, CancellationToken ct) =>
        (await _strokes.DeleteManyAsync(s => s.BoardId == boardId, ct)).DeletedCount;
}
