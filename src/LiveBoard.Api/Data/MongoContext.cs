using LiveBoard.Api.Configuration;
using LiveBoard.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace LiveBoard.Api.Data;

public sealed class MongoContext
{
    public MongoContext(IMongoClient client, IOptions<MongoSettings> settings)
    {
        Database = client.GetDatabase(settings.Value.Database);
        Boards = Database.GetCollection<Board>("boards");
        Cards = Database.GetCollection<Card>("cards");
        Activity = Database.GetCollection<ActivityEntry>("activity");
        Users = Database.GetCollection<UserProfile>("users");
    }

    public IMongoDatabase Database { get; }
    public IMongoCollection<Board> Boards { get; }
    public IMongoCollection<Card> Cards { get; }
    public IMongoCollection<ActivityEntry> Activity { get; }
    public IMongoCollection<UserProfile> Users { get; }

    /// <summary>Idempotent: createIndexes is a no-op when an identical index exists.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await Boards.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Board>(Builders<Board>.IndexKeys.Ascending("members.userId")),
            new CreateIndexModel<Board>(Builders<Board>.IndexKeys.Ascending("pendingInvites.email")),
        ], ct);

        await Cards.Indexes.CreateOneAsync(new CreateIndexModel<Card>(
            Builders<Card>.IndexKeys.Ascending(c => c.BoardId).Ascending(c => c.ColumnId).Ascending(c => c.Position)), cancellationToken: ct);

        await Activity.Indexes.CreateOneAsync(new CreateIndexModel<ActivityEntry>(
            Builders<ActivityEntry>.IndexKeys.Ascending(a => a.BoardId).Descending(a => a.CreatedAt)), cancellationToken: ct);

        await Users.Indexes.CreateOneAsync(new CreateIndexModel<UserProfile>(
            Builders<UserProfile>.IndexKeys.Ascending(u => u.Email)), cancellationToken: ct);
    }
}

public sealed class MongoIndexInitializer(MongoContext context, ILogger<MongoIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await context.EnsureIndexesAsync(cancellationToken);
        logger.LogInformation("MongoDB indexes ensured on database {Database}", context.Database.DatabaseNamespace.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class MongoHealthCheck(MongoContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken ct = default)
    {
        try
        {
            await context.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB is unreachable.", ex);
        }
    }
}

public static class MongoServiceCollectionExtensions
{
    public static IServiceCollection AddMongo(this IServiceCollection services)
    {
        MongoMappings.Register();

        services.AddSingleton<IMongoClient>(sp =>
            new MongoClient(sp.GetRequiredService<IOptions<MongoSettings>>().Value.ConnectionString));
        services.AddSingleton<MongoContext>();
        services.AddSingleton<BoardRepository>();
        services.AddSingleton<CardRepository>();
        services.AddSingleton<ActivityRepository>();
        services.AddSingleton<UserRepository>();
        services.AddHostedService<MongoIndexInitializer>();
        return services;
    }
}
