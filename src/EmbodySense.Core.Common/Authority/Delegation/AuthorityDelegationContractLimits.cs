namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Defines finite schema-1 bounds for delegated-authority evidence.</summary>
public static class AuthorityDelegationContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum stable envelope or operation identifier length.</summary>
    public const int MaxIdentifierCharacters = 128;

    /// <summary>Gets the maximum target-class or operation-class token length.</summary>
    public const int MaxClassTokenCharacters = 128;

    /// <summary>Gets the number of authority-ceiling dimensions represented by a subset proof.</summary>
    public const int MaxNarrowingDimensions = 7;

    /// <summary>Gets the maximum validation errors returned by one operation.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum safe validation path length.</summary>
    public const int MaxErrorPathCharacters = 256;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
