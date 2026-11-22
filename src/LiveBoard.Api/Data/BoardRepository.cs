using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class BoardRepository(MongoContext context)
{
    private const int MaxAttempts = 3;
    private readonly IMongoCollection<Board> _boards = context.Boards;

    public async Task<Board?> GetAsync(Guid id, CancellationToken ct) =>
        await _boards.Find(b => b.Id == id).FirstOrDefaultAsync(ct);

    public Task<List<Board>> ListForMemberAsync(string userId, CancellationToken ct) =>
        _boards.Find(Builders<Board>.Filter.Eq("members.userId", userId))
            .SortByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

    public Task<List<Board>> ListWithInviteAsync(string email, CancellationToken ct) =>
        _boards.Find(Builders<Board>.Filter.Eq("pendingInvites.email", email)).ToListAsync(ct);

    public Task InsertAsync(Board board, CancellationToken ct) =>
        _boards.InsertOneAsync(board, cancellationToken: ct);

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        _boards.DeleteOneAsync(b => b.Id == id, ct);

    /// <summary>
    /// Load, mutate, then replace only if nobody else saved in between (compare-and-swap on Version).
    /// Board-level changes (columns, members) are independent of each other, so on a lost race we simply
    /// reload and re-apply instead of bothering the user. The mutation re-runs authorization and rules
    /// against the fresh state each attempt.
    /// </summary>
    public async Task<Board> UpdateAsync(Guid id, Action<Board> mutate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var board = await GetAsync(id, ct) ?? throw new EntityNotFoundException("Board not found.");
            var expectedVersion = board.Version;

            mutate(board);

            var result = await _boards.ReplaceOneAsync(
                b => b.Id == id && b.Version == expectedVersion, board, cancellationToken: ct);

            if (result.MatchedCount == 1)
                return board;
            if (attempt == MaxAttempts)
                throw new ConcurrencyConflictException(id, expectedVersion, -1);
        }
    }
}
