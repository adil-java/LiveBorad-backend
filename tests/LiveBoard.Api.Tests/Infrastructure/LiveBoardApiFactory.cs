using EphemeralMongo;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LiveBoard.Api.Tests.Infrastructure;

/// <summary>
/// Runs the real API in memory against a throwaway MongoDB server (downloaded and started by EphemeralMongo,
/// no Docker needed). Firebase is replaced by locally signed tokens with the same issuer/audience/claims.
/// </summary>
public sealed class LiveBoardApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private IMongoRunner? _mongo;

    // Keep database files next to the test binaries (same drive as the repo) instead of the system temp folder.
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "mongo-data", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        _mongo = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            DataDirectory = _dataDirectory,
            AdditionalArguments = ["--quiet", "--wiredTigerCacheSizeGB", "0.25"],
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Mongo:ConnectionString", _mongo!.ConnectionString);
        builder.UseSetting("Mongo:Database", $"liveboard-tests-{Guid.NewGuid():N}");
        builder.UseSetting("Firebase:ProjectId", TestTokens.ProjectId);

        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Validate with the test key instead of fetching Google's signing keys.
                options.Authority = null;
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            }));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _mongo?.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // mongod may still be releasing files; the next run uses a fresh folder anyway.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<LiveBoardApiFactory>
{
    public const string Name = "api";
}
