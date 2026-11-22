using LiveBoard.Api.Data;
using LiveBoard.Domain;

namespace LiveBoard.Api.Auth;

/// <summary>Caller is a member but lacks the required role (maps to HTTP 403).</summary>
public sealed class AccessDeniedException(string message) : Exception(message);

public sealed class BoardAccess(BoardRepository boards)
{
    /// <summary>Loads a board and checks the caller's role. Non-members get 404 so board IDs cannot be probed.</summary>
    public async Task<(Board Board, BoardRole Role)> RequireAsync(Guid boardId, string userId, BoardRole minimum, CancellationToken ct)
    {
        var board = await boards.GetAsync(boardId, ct) ?? throw new EntityNotFoundException("Board not found.");
        return (board, Require(board, userId, minimum));
    }

    public static BoardRole Require(Board board, string userId, BoardRole minimum)
    {
        var role = board.RoleOf(userId) ?? throw new EntityNotFoundException("Board not found.");
        if (role < minimum)
            throw new AccessDeniedException($"This action requires the {minimum} role on this board.");
        return role;
    }
}
