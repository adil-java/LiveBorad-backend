using System.Text.Json.Serialization;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Configuration;
using LiveBoard.Api.Data;
using LiveBoard.Api.Features;
using LiveBoard.Api.Infrastructure;
using LiveBoard.Api.Realtime;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Hosts like Render inject the port to listen on.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---- Configuration (validated at startup so misconfiguration fails fast) ----
builder.Services.AddOptions<MongoSettings>().BindConfiguration(MongoSettings.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<FirebaseSettings>().BindConfiguration(FirebaseSettings.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CorsSettings>().BindConfiguration(CorsSettings.Section);

// ---- Infrastructure ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMongo();
builder.Services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongo");

// ---- Auth ----
builder.Services.AddFirebaseAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddScoped<BoardAccess>();

// ---- Real-time ----
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<BoardEvents>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- HTTP ----
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IOptions<CorsSettings>>((cors, settings) =>
    cors.AddDefaultPolicy(policy => policy
        .WithOrigins(settings.Value.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials())); // SignalR's browser client sends credentials on cross-origin negotiate

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapHealthChecks("/health");

app.MapGroup("/api")
    .RequireAuthorization()
    .MapMeEndpoints()
    .MapBoardEndpoints()
    .MapColumnEndpoints()
    .MapCardEndpoints()
    .MapMemberEndpoints()
    .MapActivityEndpoints();

app.MapHub<BoardHub>("/hubs/board");

app.Run();

public partial class Program;
