using LiveBoard.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace LiveBoard.Api.Data;

/// <summary>
/// Maps domain types to BSON without putting Mongo attributes in the domain project.
/// Registration is process-wide in the driver, so it runs once.
/// </summary>
public static class MongoMappings
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

        ConventionRegistry.Register(
            "LiveBoard",
            new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new EnumRepresentationConvention(BsonType.String),
                new IgnoreExtraElementsConvention(true), // tolerate fields added by newer versions during rolling deploys
            },
            type => type.Namespace?.StartsWith("LiveBoard", StringComparison.Ordinal) == true);

        BsonClassMap.TryRegisterClassMap<Board>(map =>
        {
            map.AutoMap();
            map.MapIdMember(b => b.Id);
            // Expose-as-read-only collections are backed by private fields; persist the fields.
            map.MapField("_members").SetElementName("members");
            map.MapField("_columns").SetElementName("columns");
            map.MapField("_pendingInvites").SetElementName("pendingInvites");
        });

        BsonClassMap.TryRegisterClassMap<BoardMember>(map => map.AutoMap());
        BsonClassMap.TryRegisterClassMap<Column>(map => map.AutoMap());
        BsonClassMap.TryRegisterClassMap<PendingInvite>(map => map.AutoMap());

        BsonClassMap.TryRegisterClassMap<Card>(map =>
        {
            map.AutoMap();
            map.MapIdMember(c => c.Id);
        });

        BsonClassMap.TryRegisterClassMap<ActivityEntry>(map =>
        {
            map.AutoMap();
            map.MapIdMember(a => a.Id);
        });

        BsonClassMap.TryRegisterClassMap<ChatMessage>(map =>
        {
            map.AutoMap();
            map.MapIdMember(m => m.Id);
        });

        BsonClassMap.TryRegisterClassMap<Stroke>(map =>
        {
            map.AutoMap();
            map.MapIdMember(s => s.Id);
        });

        BsonClassMap.TryRegisterClassMap<Room>(map =>
        {
            map.AutoMap();
            map.MapIdMember(r => r.Id);
        });

        BsonClassMap.TryRegisterClassMap<UserProfile>(map =>
        {
            map.AutoMap();
            map.MapIdMember(u => u.Id);
        });
    }
}
