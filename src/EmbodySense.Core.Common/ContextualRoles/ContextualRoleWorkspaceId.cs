namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Validates canonical, non-secret workspace scope identifiers.</summary>
public static class ContextualRoleWorkspaceId
{
    private const string Prefix = "workspace-sha256:";

    /// <summary>Determines whether a value is the exact canonical workspace SHA-256 scope identifier.</summary>
    /// <param name="value">The candidate workspace identifier.</param>
    /// <returns><see langword="true"/> only for the fixed prefix followed by 64 lowercase hexadecimal characters.</returns>
    public static bool IsValid(string? value)
        => value?.Length == Prefix.Length + ContextualRoleLimits.Sha256HexCharacters
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
