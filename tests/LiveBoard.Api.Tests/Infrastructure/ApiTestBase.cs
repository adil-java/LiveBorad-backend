using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveBoard.Api.Features;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveBoard.Api.Tests.Infrastructure;

public sealed record TestUser(string Id, string Email, string Name, string Token, HttpClient Http);

[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(LiveBoardApiFactory factory)
{
    protected static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected LiveBoardApiFactory Factory { get; } = factory;

    // ---- Users ---------------------------------------------------------------------------------

    /// <summary>A fresh user with a unique ID/email, so tests sharing one database never collide.</summary>
    protected async Task<TestUser> CreateUserAsync(string name = "user", bool emailVerified = true, bool sync = true, string? email = null)
    {
        var id = Guid.NewGuid().ToString("N");
        email ??= $"{name}-{id[..8]}@example.com";
        var user = NewUser(id, email, name, emailVerified);
        if (sync)
            await SyncAsync(user);
        return user;
    }

    protected TestUser NewUser(string id, string email, string name, bool emailVerified)
    {
        var token = TestTokens.Create(id, email, name, emailVerified);
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new TestUser(id, email, name, token, http);
    }

    /// <summary>A guest, as signed in with Firebase anonymous auth to join a room by code.</summary>
    protected TestUser NewGuest(string name = "guest")
    {
        var id = Guid.NewGuid().ToString("N");
        var token = TestTokens.CreateGuest(id);
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new TestUser(id, string.Empty, name, token, http);
    }

    protected static async Task<MeDto> SyncAsync(TestUser user)
    {
        var response = await user.Http.PostAsync("/api/me/sync", null);
        return await ReadAsync<MeDto>(response);
    }

    // ---- API helpers ---------------------------------------------------------------------------

    protected static async Task<BoardSummaryDto> CreateBoardAsync(TestUser owner, string name = "Test board") =>
        await ReadAsync<BoardSummaryDto>(await owner.Http.PostAsJsonAsync("/api/boards", new CreateBoardRequest(name), Json), HttpStatusCode.Created);

    protected static async Task<BoardDetailDto> GetBoardAsync(TestUser user, Guid boardId) =>
        await ReadAsync<BoardDetailDto>(await user.Http.GetAsync($"/api/boards/{boardId}"));

    protected static async Task AddMemberAsync(TestUser owner, Guid boardId, TestUser member, BoardRole role)
    {
        var members = await ReadAsync<MembersDto>(
            await owner.Http.PostAsJsonAsync($"/api/boards/{boardId}/members", new InviteMemberRequest(member.Email, role), Json));
        Assert.Contains(members.Members, m => m.UserId == member.Id && m.Role == role);
    }

    protected static async Task<CardDto> CreateCardAsync(TestUser user, Guid boardId, Guid columnId, string title, string? connectionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{boardId}/cards")
        {
            Content = JsonContent.Create(new CreateCardRequest(columnId, title, null), options: Json),
        };
        if (connectionId is not null)
            request.Headers.Add(BoardEvents.ConnectionIdHeader, connectionId);
        return await ReadAsync<CardDto>(await user.Http.SendAsync(request), HttpStatusCode.Created);
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        if (response.StatusCode != expected)
            Assert.Fail($"Expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // ---- SignalR helpers -----------------------------------------------------------------------

    protected async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var server = Factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/board"), options =>
            {
                // TestServer has no real sockets; long polling runs over its in-memory HTTP handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>Subscribe before triggering the action; await the returned task afterwards.</summary>
    protected static Task<T> NextEvent<T>(HubConnection connection, string method, Func<T, bool>? match = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T>(method, value =>
        {
            if (match?.Invoke(value) ?? true)
                tcs.TrySetResult(value);
        });
        return tcs.Task.WaitAsync(EventTimeout);
    }

    protected static Task<(T1, T2)> NextEvent<T1, T2>(HubConnection connection, string method, Func<T1, T2, bool>? match = null)
    {
        var tcs = new TaskCompletionSource<(T1, T2)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T1, T2>(method, (a, b) =>
        {
            if (match?.Invoke(a, b) ?? true)
                tcs.TrySetResult((a, b));
        });
        return tcs.Task.WaitAsync(EventTimeout);
    }
}
