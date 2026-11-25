using LiveBoard.Api.Features;
using LiveBoard.Api.Realtime;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LiveBoard.Api.Tests;

public class CallTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(TestUser User, HubConnection Hub)> OnBoardAsync(TestUser owner, Guid boardId, string name, BoardRole role)
    {
        var user = await CreateUserAsync(name);
        await AddMemberAsync(owner, boardId, user, role);
        var hub = await ConnectAsync(user);
        await hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", boardId);
        return (user, hub);
    }

    private static Task<List<CallParticipantDto>> JoinCall(HubConnection hub, bool mic = true, bool camera = false) =>
        hub.InvokeAsync<List<CallParticipantDto>>("JoinCall", mic, camera);

    [Fact]
    public async Task Joiner_GetsExistingParticipants_AndEveryoneOnTheBoardSeesWhoIsInTheCall()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, bobHub) = await OnBoardAsync(owner, board.Id, "bob", BoardRole.Editor);
        var (_, carolHub) = await OnBoardAsync(owner, board.Id, "carol", BoardRole.Editor); // on the board, not in the call
        await using var _b = bobHub;
        await using var _c = carolHub;

        Assert.Empty(await JoinCall(aliceHub)); // first in: nobody to call

        var carolSeesTwo = NextEvent<Guid, List<CallParticipantDto>>(carolHub, "CallParticipantsChanged", (_, p) => p.Count == 2);
        var existing = await JoinCall(bobHub, mic: false, camera: true);

        var alice = Assert.Single(existing);
        Assert.Equal("alice", alice.DisplayName);
        Assert.Equal(aliceHub.ConnectionId, alice.ConnectionId);

        var (_, participants) = await carolSeesTwo;
        Assert.Equal(["alice", "bob"], participants.Select(p => p.DisplayName)); // oldest first
        Assert.False(participants[1].MicOn);
        Assert.True(participants[1].CameraOn);
    }

    [Fact]
    public async Task FirstJoin_LogsCallStarted_ButLaterJoinsDoNot()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, bobHub) = await OnBoardAsync(owner, board.Id, "bob", BoardRole.Viewer);
        await using var _b = bobHub;

        var started = NextEvent<ActivityDto>(aliceHub, "ActivityAdded", a => a.Type == ActivityType.CallStarted);
        await JoinCall(aliceHub);
        Assert.Equal("started a call", (await started).Summary);

        await JoinCall(bobHub); // viewers may join calls

        var feed = await ReadAsync<List<ActivityDto>>(await owner.Http.GetAsync($"/api/boards/{board.Id}/activity"));
        Assert.Single(feed, a => a.Type == ActivityType.CallStarted);
    }

    [Fact]
    public async Task Signals_AreRelayedBetweenParticipants_WithTheSendersConnectionId()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, bobHub) = await OnBoardAsync(owner, board.Id, "bob", BoardRole.Editor);
        await using var _b = bobHub;
        await JoinCall(aliceHub);
        var existing = await JoinCall(bobHub);

        var aliceGets = new TaskCompletionSource<(string From, string Type, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        aliceHub.On<Guid, string, string, string>("CallSignal", (_, from, type, payload) => aliceGets.TrySetResult((from, type, payload)));

        await bobHub.InvokeAsync("SendSignal", existing[0].ConnectionId, "offer", "{\"sdp\":\"v=0...\"}");

        var signal = await aliceGets.Task.WaitAsync(EventTimeout);
        Assert.Equal(bobHub.ConnectionId, signal.From);
        Assert.Equal("offer", signal.Type);
        Assert.Equal("{\"sdp\":\"v=0...\"}", signal.Payload);
    }

    [Fact]
    public async Task Signals_DoNotReachPeopleOutsideTheCall_OrOnAnotherBoard()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        var otherBoard = await CreateBoardAsync(owner, "Other");
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, watcherHub) = await OnBoardAsync(owner, board.Id, "watcher", BoardRole.Editor); // on board, not in call
        var (_, strangerHub) = await OnBoardAsync(owner, otherBoard.Id, "elsewhere", BoardRole.Editor); // in a call on another board
        await using var _w = watcherHub;
        await using var _s = strangerHub;
        await JoinCall(aliceHub);
        await JoinCall(strangerHub);

        var leaked = false;
        watcherHub.On<Guid, string, string, string>("CallSignal", (_, _, _, _) => leaked = true);
        strangerHub.On<Guid, string, string, string>("CallSignal", (_, _, _, _) => leaked = true);

        await aliceHub.InvokeAsync("SendSignal", watcherHub.ConnectionId, "offer", "{}");
        await aliceHub.InvokeAsync("SendSignal", strangerHub.ConnectionId, "offer", "{}");

        // Not in the call yet: cannot send at all.
        await Assert.ThrowsAsync<HubException>(() => watcherHub.InvokeAsync("SendSignal", aliceHub.ConnectionId, "offer", "{}"));
        // A round trip on each connection after the sends: anything relayed to them would have arrived by now.
        await watcherHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await strangerHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", otherBoard.Id);
        Assert.False(leaked);
    }

    [Theory]
    [InlineData("hack", "{}")]
    [InlineData("offer", "")]
    public async Task InvalidSignals_AreRejected(string type, string payload)
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await using var hub = await ConnectAsync(owner);
        await hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        await JoinCall(hub);

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("SendSignal", "someone", type, payload));
    }

    [Fact]
    public async Task Call_IsLimitedInSize()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var hubs = new List<HubConnection>();
        try
        {
            for (var i = 0; i < BoardHub.MaxCallParticipants; i++)
            {
                var (_, hub) = await OnBoardAsync(owner, board.Id, $"user{i}", BoardRole.Editor);
                hubs.Add(hub);
                await JoinCall(hub);
            }

            var (_, late) = await OnBoardAsync(owner, board.Id, "late", BoardRole.Editor);
            hubs.Add(late);
            var error = await Assert.ThrowsAsync<HubException>(() => JoinCall(late));
            Assert.Contains("full", error.Message);
        }
        finally
        {
            foreach (var hub in hubs)
                await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task SimultaneousJoins_StillSeeEachOther()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, bobHub) = await OnBoardAsync(owner, board.Id, "bob", BoardRole.Editor);
        await using var _b = bobHub;

        var results = await Task.WhenAll(JoinCall(aliceHub), JoinCall(bobHub));

        // Exactly one of them must see the other, so exactly one sends the offer.
        Assert.Equal(1, results.Count(r => r.Count == 1));
        Assert.Equal(1, results.Count(r => r.Count == 0));
    }

    [Fact]
    public async Task LeavingOrDisconnecting_RemovesTheParticipant_AndStateUpdatesAreBroadcast()
    {
        var owner = await CreateUserAsync("alice");
        var board = await CreateBoardAsync(owner);
        await using var aliceHub = await ConnectAsync(owner);
        await aliceHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        var (_, bobHub) = await OnBoardAsync(owner, board.Id, "bob", BoardRole.Editor);
        var (_, carolHub) = await OnBoardAsync(owner, board.Id, "carol", BoardRole.Editor);
        await using var _c = carolHub;
        await JoinCall(aliceHub);
        await JoinCall(bobHub);
        await JoinCall(carolHub);

        var bobMuted = NextEvent<Guid, List<CallParticipantDto>>(aliceHub, "CallParticipantsChanged",
            (_, p) => p.Any(x => x.DisplayName == "bob" && !x.MicOn && x.ScreenSharing));
        await bobHub.InvokeAsync("UpdateCallState", false, false, true);
        await bobMuted;

        var carolLeft = NextEvent<Guid, List<CallParticipantDto>>(aliceHub, "CallParticipantsChanged", (_, p) => p.Count == 2);
        await carolHub.InvokeAsync("LeaveCall");
        Assert.DoesNotContain("carol", (await carolLeft).Item2.Select(p => p.DisplayName));

        var bobGone = NextEvent<Guid, List<CallParticipantDto>>(aliceHub, "CallParticipantsChanged", (_, p) => p.Count == 1);
        await bobHub.DisposeAsync(); // closed the tab mid-call
        Assert.Equal(["alice"], (await bobGone).Item2.Select(p => p.DisplayName));

        // Someone opening the board later is told a call is running.
        var (_, daveHub) = await OnBoardAsync(owner, board.Id, "dave", BoardRole.Viewer);
        await using var _d = daveHub;
        var running = NextEvent<Guid, List<CallParticipantDto>>(daveHub, "CallParticipantsChanged");
        await daveHub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);
        Assert.Equal(["alice"], (await running).Item2.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task UpdatingCallState_RequiresBeingInTheCall()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        await using var hub = await ConnectAsync(owner);
        await hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id);

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("UpdateCallState", true, true, false));
    }

    [Fact]
    public async Task IceServers_DefaultToPublicStun()
    {
        var user = await CreateUserAsync();

        var servers = await ReadAsync<List<IceServerDto>>(await user.Http.GetAsync("/api/calls/ice-servers"));

        Assert.Contains(servers, s => s.Urls.Any(u => u.StartsWith("stun:")));
    }
}
