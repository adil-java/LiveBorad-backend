namespace LiveBoard.Domain;

internal static class Guard
{
    public static string RequiredText(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new DomainValidationException($"{field} is required.");
        if (trimmed.Length > maxLength)
            throw new DomainValidationException($"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static string? OptionalText(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > maxLength)
            throw new DomainValidationException($"{field} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static string Email(string? value)
    {
        var email = RequiredText(value, "Email", 254).ToLowerInvariant();
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1 || email.Contains(' '))
            throw new DomainValidationException("Email is not a valid address.");
        return email;
    }
}
