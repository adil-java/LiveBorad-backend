using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/me/sync", SyncAsync)
            .WithTags("Me")
            .WithSummary("Create or refresh the caller's profile from their Firebase token and accept pending invites.")
            .WithDescription("Clients call this after every sign-in. Invites are only accepted for verified email addresses.");
        return api;
    }

    private static async Task<Ok<MeDto>> SyncAsync(
        ClaimsPrincipal user, UserRepository users, BoardRepository boards, BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var email = user.GetEmail() ?? throw new DomainValidationException("Your account has no email address.");
        var name = user.FindFirstValue(FirebaseClaims.Name);
        var now = clock.GetUtcNow().UtcDateTime;

        var profile = await users.GetAsync(userId, ct);
        if (profile is null)
            profile = UserProfile.Create(userId, email, name, user.IsEmailVerified(), now);
        else
            profile.Refresh(email, name, user.IsEmailVerified(), now);
        await users.UpsertAsync(profile, ct);

        var joined = new List<Guid>();
        if (profile.EmailVerified)
        {
            foreach (var invited in await boards.ListWithInviteAsync(profile.Email, ct))
            {
                Board board;
                var accepted = false;
                try
                {
                    board = await boards.UpdateAsync(invited.Id, b => accepted = b.AcceptInvite(userId, profile.Email, now), ct);
                }
                catch (EntityNotFoundException)
                {
                    continue; // board deleted meanwhile
                }

                if (!accepted)
                    continue;

                joined.Add(board.Id);
                var profiles = await users.GetManyAsync(board.Members.Select(m => m.UserId), ct);
                await events.Everyone(board.Id).MembersChanged(board.Id, board.ToMembers(BoardRole.Viewer, profiles).Members);
                await events.LogAsync(board.Id, user, ActivityType.MemberAdded, "joined the board", null, ct);
            }
        }

        return TypedResults.Ok(new MeDto(profile.Id, profile.Email, profile.DisplayName, profile.EmailVerified, joined));
    }
}
