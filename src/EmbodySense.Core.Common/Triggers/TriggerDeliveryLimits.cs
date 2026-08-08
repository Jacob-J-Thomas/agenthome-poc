namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Defines every bound used by the schema-version-1 trigger-delivery contract.
/// </summary>
public static class TriggerDeliveryLimits
{
    /// <summary>Gets the maximum delivery identifier length.</summary>
    public const int MaxDeliveryIdCharacters = 128;

    /// <summary>Gets the maximum deduplication identifier length.</summary>
    public const int MaxDeduplicationIdCharacters = 128;

    /// <summary>Gets the maximum loop identifier length.</summary>
    public const int MaxLoopIdCharacters = 120;

    /// <summary>Gets the maximum workspace identifier length.</summary>
    public const int MaxWorkspaceIdCharacters = 120;

    /// <summary>Gets the maximum role identifier length.</summary>
    public const int MaxRoleIdCharacters = 120;

    /// <summary>Gets the maximum surface identifier length.</summary>
    public const int MaxSurfaceIdCharacters = 64;

    /// <summary>Gets the maximum governed payload-reference length.</summary>
    public const int MaxPayloadReferenceCharacters = 256;

    /// <summary>Gets the maximum conversation identifier length.</summary>
    public const int MaxConversationIdCharacters = 128;

    /// <summary>Gets the maximum conversation-version length.</summary>
    public const int MaxConversationVersionCharacters = 128;

    /// <summary>Gets the maximum inline payload length.</summary>
    public const int MaxInlinePayloadBytes = 32 * 1024;

    /// <summary>Gets the maximum canonical envelope length.</summary>
    public const int MaxCanonicalDocumentUtf8Bytes = 128 * 1024;

    /// <summary>Gets the maximum redelivery count.</summary>
    public const int MaxRedeliveryCount = 1_024;

    /// <summary>Gets the maximum supported loop definition revision.</summary>
    public const int MaxLoopDefinitionVersion = 1_000_000_000;

    /// <summary>Gets the number of lowercase hexadecimal characters in a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;

    /// <summary>Gets the maximum interval represented by one delivery envelope.</summary>
    public static TimeSpan MaxTemporalHorizon { get; } = TimeSpan.FromDays(30);

    /// <summary>Gets the maximum age of boundary evidence accepted for a new admission.</summary>
    public static TimeSpan MaxAuthorityEvidenceAge { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the maximum age of a received delivery accepted for a new admission.</summary>
    public static TimeSpan MaxAdmissionAge { get; } = TimeSpan.FromDays(1);
}
