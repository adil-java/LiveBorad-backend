using System.ComponentModel.DataAnnotations;

namespace LiveBoard.Api.Configuration;

public sealed class MongoSettings
{
    public const string Section = "Mongo";

    [Required] public string ConnectionString { get; set; } = string.Empty;
    [Required] public string Database { get; set; } = string.Empty;
}

public sealed class FirebaseSettings
{
    public const string Section = "Firebase";

    /// <summary>Firebase project ID. Tokens must be issued by https://securetoken.google.com/{ProjectId} for this audience.</summary>
    [Required] public string ProjectId { get; set; } = string.Empty;
}

public sealed class CallSettings
{
    public const string Section = "Calls";

    /// <summary>
    /// STUN/TURN servers handed to browsers. STUN lets most peers connect directly; TURN relays media for networks
    /// that block direct connections. Configure TURN credentials outside the repo (user-secrets / environment).
    /// </summary>
    public IceServerSettings[] IceServers { get; set; } = [];
}

public sealed class IceServerSettings
{
    public string[] Urls { get; set; } = [];
    public string? Username { get; set; }
    public string? Credential { get; set; }
}

public sealed class CorsSettings
{
    public const string Section = "Cors";

    /// <summary>Exact origins of the frontend, e.g. https://liveboard.onrender.com. Wildcards are not allowed with credentials.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
