namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Validates stable contextual-role identifiers.</summary>
public static class ContextualRoleId
{
    /// <summary>Determines whether a value is a bounded, lowercase ASCII role identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><see langword="true"/> when the value is safe for stable role attribution.</returns>
    public static bool IsValid(string? value) => value is { Length: > 0 and <= ContextualRoleLimits.MaxIdentifierCharacters }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}
