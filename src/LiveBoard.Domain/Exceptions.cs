namespace LiveBoard.Domain;

public abstract class DomainException(string message) : Exception(message);

/// <summary>Input breaks a business rule (maps to HTTP 400).</summary>
public sealed class DomainValidationException(string message) : DomainException(message);

/// <summary>The entity does not exist, or the caller may not know it exists (maps to HTTP 404).</summary>
public sealed class EntityNotFoundException(string message) : DomainException(message);

/// <summary>A change was based on a stale version because someone else changed the entity first (maps to HTTP 409).</summary>
public sealed class ConcurrencyConflictException(Guid entityId, int expectedVersion, int actualVersion)
    : DomainException($"Entity {entityId} was changed by someone else (expected v{expectedVersion}, current v{actualVersion}).")
{
    public Guid EntityId { get; } = entityId;
    public int ExpectedVersion { get; } = expectedVersion;
    public int ActualVersion { get; } = actualVersion;
}
