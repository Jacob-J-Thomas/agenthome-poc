namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Defines bounded schema-1 limits for durable Human Input waiting-checkpoint contracts.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointContractLimits
{
    /// <summary>Gets the only supported checkpoint schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the lowercase SHA-256 hexadecimal character count.</summary>
    public const int Sha256HexCharacters = 64;

    /// <summary>Gets the maximum append-only checkpoint evidence entries.</summary>
    public const int MaxEvidenceEntries = 3;

    /// <summary>Gets the maximum compact canonical JSON character count.</summary>
    public const int MaxJsonCharacters = 131_072;
}
