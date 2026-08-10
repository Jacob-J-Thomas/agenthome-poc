namespace EmbodySense.Core.Common.HumanInput.Lifecycle;

/// <summary>Defines finite schema-version-1 bounds for durable Human Input request lifecycle contracts.</summary>
public static class HumanInputRequestLifecycleContractLimits
{
    /// <summary>The only supported experimental contract schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The maximum number of characters in a workspace-global operation identifier.</summary>
    public const int MaxOperationIdCharacters = HumanInputLimits.MaxIdentifierCharacters;

    /// <summary>The number of lowercase hexadecimal characters in a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = HumanInputLimits.Sha256HexCharacters;

    /// <summary>The maximum optimistic lifecycle version that remains exact in interoperable JSON number domains.</summary>
    public const long MaxLifecycleVersion = 9_007_199_254_740_991;

    /// <summary>The maximum number of reminders retained for one request.</summary>
    public const int MaxReminderCount = 64;

    /// <summary>The maximum immutable request versions retained for one request.</summary>
    public const int MaxRequestVersionsPerRequest = 32;

    /// <summary>The maximum append-only lifecycle operations retained for one request.</summary>
    public const int MaxOperationsPerRequest = 256;

    /// <summary>The maximum request heads retained by one workspace store.</summary>
    public const int MaxRequestsPerStore = 1_024;

    /// <summary>The maximum immutable request versions retained by one workspace store.</summary>
    public const int MaxRequestVersionsPerStore = 4_096;

    /// <summary>The maximum append-only lifecycle operations retained by one workspace store.</summary>
    public const int MaxOperationsPerStore = 8_192;

    /// <summary>The maximum UTF-8 bytes accepted for one authenticated request-store document.</summary>
    public const int MaxStoreDocumentUtf8Bytes = 4 * 1024 * 1024;

    /// <summary>The maximum JSON depth accepted for one request-store document.</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>The maximum number of structured validation errors returned by one call.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>The maximum number of characters in one safe schema-relative error path.</summary>
    public const int MaxErrorPathCharacters = 192;
}
