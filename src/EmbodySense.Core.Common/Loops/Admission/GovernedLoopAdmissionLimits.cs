namespace EmbodySense.Core.Common.Loops.Admission;

/// <summary>Defines finite schema-version-1 bounds for governed-loop admission contracts.</summary>
public static class GovernedLoopAdmissionLimits
{
    /// <summary>Gets the only supported experimental admission schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum operation or request identifier length.</summary>
    public const int MaxIdentifierCharacters = 128;

    /// <summary>Gets the maximum canonical surface token length.</summary>
    public const int MaxSurfaceCharacters = 64;

    /// <summary>Gets the maximum number of exact evidence references retained with one disposition.</summary>
    public const int MaxEvidenceReferences = 7;

    /// <summary>Gets the maximum number of required root capability-policy violations retained in one denial proof.</summary>
    public const int MaxCapabilityDenialViolations = 64;

    /// <summary>Gets the maximum number of structured validation errors returned per call.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum safe validation-path length.</summary>
    public const int MaxErrorPathCharacters = 256;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
