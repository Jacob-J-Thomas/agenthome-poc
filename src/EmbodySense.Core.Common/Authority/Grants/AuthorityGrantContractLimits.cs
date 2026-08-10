namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Defines finite schema-version-1 bounds for revision-pinned authority grants.</summary>
public static class AuthorityGrantContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum stable grant-identifier length.</summary>
    public const int MaxGrantIdCharacters = 128;

    /// <summary>Gets the maximum workspace-global operation-identifier length.</summary>
    public const int MaxOperationIdCharacters = 128;

    /// <summary>Gets the maximum canonical grant JSON length in both characters and UTF-8 bytes.</summary>
    public const int MaxGrantJsonCharacters = 32_768;

    /// <summary>Gets the maximum retained validation errors.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum immutable revisions retained for one grant.</summary>
    public const int MaxRevisionsPerGrant = 128;

    /// <summary>Gets the maximum grants retained in one workspace ledger.</summary>
    public const int MaxGrantsPerStore = 256;

    /// <summary>Gets the maximum append-only grant operations retained in one workspace ledger.</summary>
    public const int MaxOperationsPerStore = 4_096;
}
