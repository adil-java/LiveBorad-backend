using LiveBoard.Domain;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class RoomRepository(MongoContext context)
{
    private readonly IMongoCollection<Room> _rooms = context.Rooms;

    /// <summary>Inserts the room, drawing a new code in the (astronomically unlikely) case the code is taken.</summary>
    public async Task InsertAsync(Room room, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _rooms.InsertOneAsync(room, cancellationToken: ct);
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && attempt < 5)
            {
                room.RegenerateCode();
            }
        }
    }

    public async Task<Room?> GetByCodeAsync(string code, CancellationToken ct) =>
        await _rooms.Find(r => r.Code == code).FirstOrDefaultAsync(ct);

    public Task<List<Room>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
        _rooms.Find(r => r.OwnerId == ownerId).SortByDescending(r => r.CreatedAt).ToListAsync(ct);

    public Task<long> CountByOwnerAsync(string ownerId, CancellationToken ct) =>
        _rooms.CountDocumentsAsync(r => r.OwnerId == ownerId, cancellationToken: ct);

    public async Task<bool> DeleteAsync(Guid roomId, CancellationToken ct) =>
        (await _rooms.DeleteOneAsync(r => r.Id == roomId, ct)).DeletedCount == 1;
}
