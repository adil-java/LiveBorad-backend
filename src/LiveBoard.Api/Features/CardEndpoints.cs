using System.Security.Claims;
using LiveBoard.Api.Auth;
using LiveBoard.Api.Data;
using LiveBoard.Api.Realtime;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LiveBoard.Api.Features;

public static class CardEndpoints
{
    public static RouteGroupBuilder MapCardEndpoints(this RouteGroupBuilder api)
    {
        var cards = api.MapGroup("/boards/{boardId:guid}/cards").WithTags("Cards");

        cards.MapPost("/", CreateAsync).WithSummary("Add a card at the bottom of a column. Requires Editor.");
        cards.MapPut("/{cardId:guid}", EditAsync)
            .WithSummary("Edit a card's title/description. Requires Editor.")
            .WithDescription("Send the version you last saw. If someone else changed the card first, the response is 409 with the current card.");
        cards.MapPost("/{cardId:guid}/move", MoveAsync)
            .WithSummary("Move a card between two neighbours, in the same or another column. Requires Editor.")
            .WithDescription("Returns 409 with the current card if the card or its neighbours changed since the client last saw them.");
        cards.MapDelete("/{cardId:guid}", DeleteAsync).WithSummary("Delete a card. Requires Editor.");

        return api;
    }

    private static async Task<Created<CardDto>> CreateAsync(
        Guid boardId, CreateCardRequest request, ClaimsPrincipal user, BoardAccess access, CardRepository cards,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var (board, _) = await access.RequireAsync(boardId, userId, BoardRole.Editor, ct);
        var column = board.GetColumn(request.ColumnId);

        var last = await cards.MaxPositionAsync(boardId, column.Id, null, ct);
        var card = Card.Create(boardId, column.Id, request.Title, request.Description, Ranking.Between(last, null), userId, clock.GetUtcNow().UtcDateTime);
        await cards.InsertAsync(card, ct);

        var dto = card.ToDto();
        await events.Others(boardId).CardCreated(dto);
        await events.LogAsync(boardId, user, ActivityType.CardCreated, $"added \"{card.Title}\" to {column.Name}", card.Id, ct);
        return TypedResults.Created($"/api/boards/{boardId}/cards/{card.Id}", dto);
    }

    private static async Task<Results<Ok<CardDto>, Conflict<CardDto>, NotFound>> EditAsync(
        Guid boardId, Guid cardId, EditCardRequest request, ClaimsPrincipal user, BoardAccess access, CardRepository cards,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await access.RequireAsync(boardId, userId, BoardRole.Editor, ct);

        var card = await cards.GetAsync(boardId, cardId, ct);
        if (card is null)
            return TypedResults.NotFound();

        try
        {
            card.Edit(request.Title, request.Description, request.Version, userId, clock.GetUtcNow().UtcDateTime);
        }
        catch (ConcurrencyConflictException)
        {
            return TypedResults.Conflict(card.ToDto()); // card is unchanged: the version check runs before any mutation
        }

        // Second line of defence: someone may have saved between our read and this write.
        if (!await cards.SaveContentAsync(card, request.Version, ct))
            return await ConflictOrNotFoundAsync(cards, boardId, cardId, ct);

        var dto = card.ToDto();
        await events.Others(boardId).CardUpdated(dto);
        await events.LogAsync(boardId, user, ActivityType.CardEdited, $"edited \"{card.Title}\"", card.Id, ct);
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<CardDto>, Conflict<CardDto>, NotFound>> MoveAsync(
        Guid boardId, Guid cardId, MoveCardRequest request, ClaimsPrincipal user, BoardAccess access, CardRepository cards,
        BoardEvents events, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var (board, _) = await access.RequireAsync(boardId, userId, BoardRole.Editor, ct);
        var targetColumn = board.GetColumn(request.ColumnId);

        var card = await cards.GetAsync(boardId, cardId, ct);
        if (card is null)
            return TypedResults.NotFound();
        if (card.Version != request.Version)
            return TypedResults.Conflict(card.ToDto());
        if (request.BeforeCardId == cardId || request.AfterCardId == cardId)
            throw new DomainValidationException("A card cannot be positioned relative to itself.");

        var before = await LoadNeighbourAsync(cards, boardId, request.BeforeCardId, targetColumn.Id, ct);
        var after = await LoadNeighbourAsync(cards, boardId, request.AfterCardId, targetColumn.Id, ct);

        // A missing neighbour, or neighbours now in the wrong order, means the client's view is stale.
        if ((request.BeforeCardId is not null && before is null) ||
            (request.AfterCardId is not null && after is null) ||
            (before is not null && after is not null && before.Position >= after.Position))
            return TypedResults.Conflict(card.ToDto());

        double? above = before?.Position;
        double? below = after?.Position;
        if (before is null && after is null)
            above = await cards.MaxPositionAsync(boardId, targetColumn.Id, cardId, ct); // append to the end

        if (Ranking.NeedsRebalance(above, below))
        {
            var respaced = await cards.RebalanceColumnAsync(boardId, targetColumn.Id, ct);
            await events.Everyone(boardId).CardsRepositioned(boardId,
                respaced.Select(p => new CardPositionDto(p.Key, p.Value)).ToList());
            above = respaced[before!.Id];
            below = respaced[after!.Id];
        }

        var fromColumn = board.Columns.FirstOrDefault(c => c.Id == card.ColumnId)?.Name;
        card.MoveTo(targetColumn.Id, Ranking.Between(above, below), request.Version, userId, clock.GetUtcNow().UtcDateTime);

        if (!await cards.SaveMoveAsync(card, request.Version, ct))
            return await ConflictOrNotFoundAsync(cards, boardId, cardId, ct);

        var dto = card.ToDto();
        await events.Others(boardId).CardMoved(dto);
        var summary = fromColumn == targetColumn.Name
            ? $"reordered \"{card.Title}\" in {targetColumn.Name}"
            : $"moved \"{card.Title}\" from {fromColumn} to {targetColumn.Name}";
        await events.LogAsync(boardId, user, ActivityType.CardMoved, summary, card.Id, ct);
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid boardId, Guid cardId, ClaimsPrincipal user, BoardAccess access, CardRepository cards, BoardEvents events, CancellationToken ct)
    {
        await access.RequireAsync(boardId, user.GetUserId(), BoardRole.Editor, ct);

        var card = await cards.GetAsync(boardId, cardId, ct);
        if (card is null || !await cards.DeleteAsync(boardId, cardId, ct))
            return TypedResults.NotFound();

        await events.Others(boardId).CardDeleted(boardId, cardId);
        await events.LogAsync(boardId, user, ActivityType.CardDeleted, $"deleted \"{card.Title}\"", cardId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Card?> LoadNeighbourAsync(CardRepository cards, Guid boardId, Guid? id, Guid columnId, CancellationToken ct)
    {
        if (id is not Guid neighbourId)
            return null;
        var neighbour = await cards.GetAsync(boardId, neighbourId, ct);
        return neighbour?.ColumnId == columnId ? neighbour : null;
    }

    private static async Task<Results<Ok<CardDto>, Conflict<CardDto>, NotFound>> ConflictOrNotFoundAsync(
        CardRepository cards, Guid boardId, Guid cardId, CancellationToken ct)
    {
        var current = await cards.GetAsync(boardId, cardId, ct);
        return current is null ? TypedResults.NotFound() : TypedResults.Conflict(current.ToDto());
    }
}
