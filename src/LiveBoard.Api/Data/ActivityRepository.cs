using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class ActivityRepository(MongoContext context)
{
    private readonly IMongoCollection<ActivityEntry> _activity = context.Activity;

    public Task InsertAsync(ActivityEntry entry, CancellationToken ct) =>
        _activity.InsertOneAsync(entry, cancellationToken: ct);

    /// <summary>Newest first. Keyset pagination on CreatedAt: pass the oldest timestamp you have as <paramref name="before"/>.</summary>
    public Task<List<ActivityEntry>> ListAsync(Guid boardId, DateTime? before, int limit, CancellationToken ct)
    {
        var filter = Builders<ActivityEntry>.Filter.Eq(a => a.BoardId, boardId);
        if (before is DateTime cursor)
            filter &= Builders<ActivityEntry>.Filter.Lt(a => a.CreatedAt, cursor);

        return _activity.Find(filter).SortByDescending(a => a.CreatedAt).Limit(limit).ToListAsync(ct);
    }

    public Task DeleteByBoardAsync(Guid boardId, CancellationToken ct) =>
        _activity.DeleteManyAsync(a => a.BoardId == boardId, ct);
}
