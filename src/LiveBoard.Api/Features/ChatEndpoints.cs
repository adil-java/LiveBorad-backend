using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class ChatEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder api)
    {
        var chat = api.MapGroup("/boards/{boardId:guid}/chat").WithTags("Chat");

        chat.MapGet("/", ListAsync)
            .WithSummary("Chat history, newest first. Page with ?before=<createdAt of the oldest message you have>. Requires Viewer.");
        chat.MapPost("/", SendAsync).WithSummary("Send a chat message. Every member, including viewers, can chat.");
        chat.MapDelete("/{messageId:guid}", DeleteAsync).WithSummary("Delete your own message, or any message as an owner.");

        return api;
    }

    private static async Task<Ok<List<ChatMessageDto>>> ListAsync(
        Guid boardId, DateTime? before, int? limit, ClaimsPrincipal user, BoardAccess access, ChatRepository chat, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var messages = await chat.ListAsync(boardId, before?.ToUniversalTime(), take, ct);
        return TypedResults.Ok(messages.Select(m => m.ToDto()).ToList());
    }

    private static async Task<Created<ChatMessageDto>> SendAsync(
        Guid boardId, SendChatMessageRequest request, ClaimsPrincipal user, BoardAccess access, ChatRepository chat,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Viewer, ct);

        var message = ChatMessage.Create(boardId, user.GetUserId(), user.GetDisplayName(), request.Text, clock.GetUtcNow().UtcDateTime);
        await chat.InsertAsync(message, ct);

        var dto = message.ToDto();
        await events.Others(boardId).ChatMessageAdded(dto);
        return TypedResults.Created($"/api/boards/{boardId}/chat/{message.Id}", dto);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid boardId, Guid messageId, ClaimsPrincipal user, BoardAccess access, ChatRepository chat, BoardEvents events, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var (_, role) = await access.RequireAsync(boardId, userId, BoardRole.Viewer, ct);

        var message = await chat.GetAsync(boardId, messageId, ct);
        if (message is null)
            return TypedResults.NotFound();
        if (!message.CanBeDeletedBy(userId, role))
            throw new AccessDeniedException("Only the author or a board owner can delete this message.");

        if (!await chat.DeleteAsync(boardId, messageId, ct))
            return TypedResults.NotFound();

        await events.Others(boardId).ChatMessageDeleted(boardId, messageId);
        return TypedResults.NoContent();
    }
}
