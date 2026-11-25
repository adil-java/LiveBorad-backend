using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LiveBoard.Api.Features;
using LiveBoard.Api.Tests.Infrastructure;
using LiveBoard.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LiveBoard.Api.Tests;

public class RoomTests(LiveBoardApiFactory factory) : ApiTestBase(factory)
{
    private static async Task<RoomDto> CreateRoomAsync(TestUser owner, string? name = null) =>
        await ReadAsync<RoomDto>(await owner.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest(name), Json), HttpStatusCode.Created);

    [Fact]
    public async Task CreatedRoom_HasAShareableCode_AndIsListedForItsOwner()
    {
        var owner = await CreateUserAsync("alice");

        var room = await CreateRoomAsync(owner);
        var named = await CreateRoomAsync(owner, "  Standup  ");

        Assert.Matches(new Regex("^[a-z]{3}-[a-z]{4}-[a-z]{3}$"), room.Code);
        Assert.Equal("alice's room", room.Name);
        Assert.Equal("Standup", named.Name);
        var mine = await ReadAsync<List<RoomDto>>(await owner.Http.GetAsync("/api/rooms"));
        Assert.Equal([named.Id, room.Id], mine.Select(r => r.Id));
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{1}")] // no dashes, upper case (pasted links are reduced to the code by the client and by JoinRoom)
    public async Task Rooms_CanBeLookedUpByCodeInAnyCommonForm_EvenByGuests(string format)
    {
        var owner = await CreateUserAsync();
        var room = await CreateRoomAsync(owner);
        var guest = NewGuest();
        var typed = string.Format(format, room.Code, room.Code.Replace("-", "").ToUpperInvariant());

        var found = await ReadAsync<RoomDto>(await guest.Http.GetAsync($"/api/rooms/{Uri.EscapeDataString(typed)}"));

        Assert.Equal(room.Id, found.Id);
        Assert.Equal(0, found.PeopleInCall);
    }

    [Fact]
    public async Task UnknownCode_IsNotFound()
    {
        var guest = NewGuest();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.Http.GetAsync("/api/rooms/abc-defg-hij")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.Http.GetAsync("/api/rooms/not-a-code")).StatusCode);
    }

    [Fact]
    public async Task Guests_CanOnlyUseRooms()
    {
        var owner = await CreateUserAsync();
        var board = await CreateBoardAsync(owner);
        var guest = NewGuest();

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.Http.PostAsync("/api/me/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.Http.GetAsync("/api/boards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.Http.PostAsJsonAsync("/api/boards", new CreateBoardRequest("Mine"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.Http.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest(null), Json)).StatusCode);

        // ...but they can get what a call needs to connect.
        Assert.Equal(HttpStatusCode.OK, (await guest.Http.GetAsync("/api/calls/ice-servers")).StatusCode);

        await using var hub = await ConnectAsync(guest);
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<List<PresenceUserDto>>("JoinBoard", board.Id));
    }

    [Fact]
    public async Task GuestAndMember_CanJoinTheSameRoomsCall()
    {
        var owner = await CreateUserAsync("alice");
        var room = await CreateRoomAsync(owner);
        var guest = NewGuest();

        await using var guestHub = await ConnectAsync(guest);
        var joined = await guestHub.InvokeAsync<RoomDto>("JoinRoom", $"https://liveboard.example/room/{room.Code}", "  Gina  ");
        Assert.Equal(room.Id, joined.Id);
        Assert.Empty(await guestHub.InvokeAsync<List<CallParticipantDto>>("JoinCall", true, false));

        await using var ownerHub = await ConnectAsync(owner);
        await ownerHub.InvokeAsync<RoomDto>("JoinRoom", room.Code, "ignored for members");
        var existing = await ownerHub.InvokeAsync<List<CallParticipantDto>>("JoinCall", true, true);

        var gina = Assert.Single(existing);
        Assert.Equal("Gina", gina.DisplayName);
        Assert.True(gina.IsGuest);

        var lookup = await ReadAsync<RoomDto>(await guest.Http.GetAsync($"/api/rooms/{room.Code}"));
        Assert.Equal(2, lookup.PeopleInCall);

        // No activity is logged for rooms (they have no board).
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        guestHub.On<Guid, string, string, string>("CallSignal", (_, from, _, _) => signal.TrySetResult(from));
        await ownerHub.InvokeAsync("SendSignal", gina.ConnectionId, "offer", "{}");
        Assert.Equal(ownerHub.ConnectionId, await signal.Task.WaitAsync(EventTimeout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Guests_MustGiveAName(string? name)
    {
        var owner = await CreateUserAsync();
        var room = await CreateRoomAsync(owner);

        await using var hub = await ConnectAsync(NewGuest());

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<RoomDto>("JoinRoom", room.Code, name));
    }

    [Fact]
    public async Task JoiningAnUnknownRoom_Fails()
    {
        await using var hub = await ConnectAsync(NewGuest());
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<RoomDto>("JoinRoom", "zzz-zzzz-zzz", "Gina"));
    }

    [Fact]
    public async Task OnlyTheOwner_CanEndARoom_AndEndingItDisconnectsEveryone()
    {
        var owner = await CreateUserAsync();
        var other = await CreateUserAsync();
        var room = await CreateRoomAsync(owner);

        await using var guestHub = await ConnectAsync(NewGuest());
        await guestHub.InvokeAsync<RoomDto>("JoinRoom", room.Code, "Gina");
        await guestHub.InvokeAsync<List<CallParticipantDto>>("JoinCall", true, false);
        var ended = NextEvent<Guid>(guestHub, "RoomEnded");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.Http.DeleteAsync($"/api/rooms/{room.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"/api/rooms/{room.Code}")).StatusCode);

        Assert.Equal(room.Id, await ended);
        await Assert.ThrowsAsync<HubException>(() => guestHub.InvokeAsync("UpdateCallState", true, false, false)); // no longer in it
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"/api/rooms/{room.Code}")).StatusCode);
    }

    [Fact]
    public void Codes_AreNormalizedFromWhatPeopleType()
    {
        Assert.Equal("abc-defg-hij", Room.NormalizeCode(" ABC DEFG HIJ "));
        Assert.Equal("abc-defg-hij", Room.NormalizeCode("abcdefghij"));
        Assert.Equal("abc-defg-hij", Room.NormalizeCode("http://localhost:5173/room/abc-defg-hij"));
        Assert.Null(Room.NormalizeCode("abc-defg"));
        Assert.Null(Room.NormalizeCode("lll-llll-lll")); // "l" is never used in codes
        Assert.Null(Room.NormalizeCode(null));
    }
}
