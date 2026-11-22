using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class ChatRepository(MongoContext context)
{
    private readonly IMongoCollection<ChatMessage> _messages = context.Chat;

    public Task InsertAsync(ChatMessage message, CancellationToken ct) =>
        _messages.InsertOneAsync(message, cancellationToken: ct);

    /// <summary>Newest first. Keyset pagination on CreatedAt: pass the oldest timestamp you have as <paramref name="before"/>.</summary>
    public Task<List<ChatMessage>> ListAsync(Guid boardId, DateTime? before, int limit, CancellationToken ct)
    {
        var filter = Builders<ChatMessage>.Filter.Eq(m => m.BoardId, boardId);
        if (before is DateTime cursor)
            filter &= Builders<ChatMessage>.Filter.Lt(m => m.CreatedAt, cursor);

        return _messages.Find(filter).SortByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Limit(limit).ToListAsync(ct);
    }

    public async Task<ChatMessage?> GetAsync(Guid boardId, Guid messageId, CancellationToken ct) =>
        await _messages.Find(m => m.Id == messageId && m.BoardId == boardId).FirstOrDefaultAsync(ct);

    public async Task<bool> DeleteAsync(Guid boardId, Guid messageId, CancellationToken ct) =>
        (await _messages.DeleteOneAsync(m => m.Id == messageId && m.BoardId == boardId, ct)).DeletedCount == 1;

    public Task DeleteByBoardAsync(Guid boardId, CancellationToken ct) =>
        _messages.DeleteManyAsync(m => m.BoardId == boardId, ct);
}
