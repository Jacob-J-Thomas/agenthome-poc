namespace EmbodySense.Core.Common.Inference.Profiles;

/// <summary>Defines the bounded schema-1 limits for governed model-profile and usage evidence.</summary>
public static class GovernedModelContractLimits
{
    /// <summary>The only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>The maximum length of a safe public identifier.</summary>
    public const int MaxIdentifierCharacters = 128;
    /// <summary>The maximum length of safe public purpose text.</summary>
    public const int MaxPurposeCharacters = 512;
    /// <summary>The maximum normalized provider output retained by one governed model attempt.</summary>
    public const int MaxProviderOutputCharacters = 8_000;
    /// <summary>The maximum ordered provider-stream segments retained by one governed model attempt.</summary>
    public const int MaxProviderOutputChunks = 256;
    /// <summary>The maximum number of values in any profile set.</summary>
    public const int MaxSetValues = 32;
    /// <summary>The maximum number of fallback profiles in one routing policy.</summary>
    public const int MaxFallbackProfiles = 8;
    /// <summary>The maximum number of inference-node routing entries in one admission snapshot.</summary>
    public const int MaxAdmissionEntries = 128;
    /// <summary>The maximum number of immutable transitions in one provider-attempt usage history.</summary>
    public const int MaxUsageLedgerEntries = 5;
    /// <summary>The maximum number of immutable provider-usage transitions retained in one workspace-ledger segment.</summary>
    public const int MaxWorkspaceUsageLedgerEntries = 8_192;
    /// <summary>The maximum supported token quantity in one evidence value.</summary>
    public const long MaxTokens = 1_000_000_000_000;
    /// <summary>The maximum supported integer-micros monetary quantity.</summary>
    public const long MaxCurrencyMicros = 9_000_000_000_000_000;
    /// <summary>The exact length of a lowercase SHA-256 hexadecimal digest.</summary>
    public const int Sha256Characters = 64;
}
