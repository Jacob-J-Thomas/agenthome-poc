namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Identifies one statically admitted, server-owned workspace scope without exposing a host path.</summary>
public sealed record WorkspaceActionScopeId
{
    private WorkspaceActionScopeId(string value) => Value = value;

    /// <summary>Gets the canonical scope identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded lowercase path identifier.</summary>
    public static bool TryParse(string? value, out WorkspaceActionScopeId? scopeId)
    {
        scopeId = null;
        if (!IsIdentifier(value))
        {
            return false;
        }

        scopeId = new WorkspaceActionScopeId(value!);
        return true;
    }

    private static bool IsIdentifier(string? value)
        => value is { Length: > 0 and <= WorkspaceActionContractLimits.MaxIdentifierCharacters }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    /// <inheritdoc />
    public override string ToString() => Value;
}
