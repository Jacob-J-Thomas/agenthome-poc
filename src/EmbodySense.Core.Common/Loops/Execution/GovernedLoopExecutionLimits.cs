namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Defines finite schema-1 bounds for canonical governed-loop execution evidence.</summary>
public static class GovernedLoopExecutionLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum stable identifier length.</summary>
    public const int MaxIdentifierCharacters = 120;

    /// <summary>Gets the maximum evidence-reference length.</summary>
    public const int MaxEvidenceReferenceCharacters = 256;

    /// <summary>Gets the maximum execution generation.</summary>
    public const long MaxExecutionGeneration = 1_000_000_000;

    /// <summary>Gets the maximum lifecycle, frontier, effect, or projection version.</summary>
    public const long MaxVersion = 1_000_000_000;

    /// <summary>Gets the maximum node attempt number.</summary>
    public const int MaxNodeAttempt = 1_000_000;

    /// <summary>Gets the maximum node evidence items in one frontier.</summary>
    public const int MaxFrontierNodes = 128;

    /// <summary>Gets the maximum committed incoming control edges recorded for one node execution.</summary>
    public const int MaxIncomingEdges = 512;

    /// <summary>Gets the maximum effect evidence items in one aggregate.</summary>
    public const int MaxEffects = 1_024;

    /// <summary>Gets the maximum projection evidence items in one aggregate.</summary>
    public const int MaxProjections = 1_024;

    /// <summary>Gets the maximum structured errors returned by one validation.</summary>
    public const int MaxValidationErrors = 128;

    /// <summary>Gets the maximum safe validation path length.</summary>
    public const int MaxErrorPathCharacters = 256;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
