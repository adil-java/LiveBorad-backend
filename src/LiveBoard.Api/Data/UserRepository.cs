using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class UserRepository(MongoContext context)
{
    private readonly IMongoCollection<UserProfile> _users = context.Users;

    public async Task<UserProfile?> GetAsync(string id, CancellationToken ct) =>
        await _users.Find(u => u.Id == id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, UserProfile>> GetManyAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        var users = await _users.Find(Builders<UserProfile>.Filter.In(u => u.Id, idList)).ToListAsync(ct);
        return users.ToDictionary(u => u.Id);
    }

    public async Task<UserProfile?> FindVerifiedByEmailAsync(string email, CancellationToken ct) =>
        await _users.Find(u => u.Email == email && u.EmailVerified).FirstOrDefaultAsync(ct);

    public Task UpsertAsync(UserProfile profile, CancellationToken ct) =>
        _users.ReplaceOneAsync(u => u.Id == profile.Id, profile, new ReplaceOptions { IsUpsert = true }, ct);
}
