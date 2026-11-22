using LiveBoard.Api.Auth;
using LiveBoard.Domain;
using Microsoft.AspNetCore.Diagnostics;

namespace LiveBoard.Api.Infrastructure;

/// <summary>Translates expected domain/authorization failures into RFC 7807 problem responses.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            DomainValidationException => (StatusCodes.Status400BadRequest, "The request breaks a business rule."),
            AccessDeniedException => (StatusCodes.Status403Forbidden, "You do not have permission to do this."),
            EntityNotFoundException => (StatusCodes.Status404NotFound, "Not found."),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Someone else changed this at the same time. Please retry."),
            _ => (0, string.Empty),
        };

        if (status == 0)
            return false; // unexpected: let the default handler log it and return a generic 500

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
