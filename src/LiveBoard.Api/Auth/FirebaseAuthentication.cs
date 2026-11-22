using LiveBoard.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace LiveBoard.Api.Auth;

public static class FirebaseAuthentication
{
    /// <summary>
    /// Validates Firebase ID tokens. Firebase publishes standard OpenID metadata at
    /// https://securetoken.google.com/{projectId}/.well-known/openid-configuration, so the stock JWT bearer
    /// handler can fetch and rotate Google's signing keys itself; no service-account secret is needed.
    /// </summary>
    public static IServiceCollection AddFirebaseAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through options (not at registration time) so tests and hosting config can supply the project ID.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<FirebaseSettings>>((options, firebase) =>
            {
                var projectId = firebase.Value.ProjectId;
                var issuer = $"https://securetoken.google.com/{projectId}";

                options.Authority = issuer;
                options.MapInboundClaims = false; // keep Firebase claim names: sub, email, name, email_verified
                options.TokenValidationParameters.ValidIssuer = issuer;
                options.TokenValidationParameters.ValidAudience = projectId;
                options.TokenValidationParameters.NameClaimType = FirebaseClaims.Name;

                options.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on WebSocket requests, so SignalR sends the token in the query string.
                    // Only accept it there for hub endpoints, to keep tokens out of ordinary request URLs and logs.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }
}
