namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Defines every closed bound used by schema-version-1 schedule contracts.</summary>
public static class ScheduleContractLimits
{
    /// <summary>Gets the only supported schedule schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum schedule identifier length.</summary>
    public const int MaxScheduleIdCharacters = 120;

    /// <summary>Gets the maximum claim identifier length.</summary>
    public const int MaxClaimIdCharacters = 128;

    /// <summary>Gets the maximum time-zone identifier length.</summary>
    public const int MaxTimeZoneIdCharacters = 128;

    /// <summary>Gets the maximum stable result or disposition reason-code length.</summary>
    public const int MaxReasonCodeCharacters = 64;

    /// <summary>Gets the maximum supported schedule, definition, or state revision.</summary>
    public const long MaxRevision = 1_000_000_000;

    /// <summary>Gets the maximum occurrence ordinal retained by schema version 1.</summary>
    public const long MaxOccurrenceOrdinal = 1_000_000_000_000;

    /// <summary>Gets the maximum fixed recurrence interval in seconds.</summary>
    public const long MaxFixedIntervalSeconds = 365L * 24 * 60 * 60;

    /// <summary>Gets the maximum number of missed occurrences admitted by one catch-up policy.</summary>
    public const int MaxCatchUpOccurrences = 256;

    /// <summary>Gets the maximum skipped/deferred evidence items retained in one state.</summary>
    public const int MaxDispositionEvidenceItems = 256;

    /// <summary>Gets the maximum skipped/deferred items retained in one immutable finalization plan.</summary>
    public const int MaxFinalizationEvidenceItems = 256;

    /// <summary>Gets the maximum terminal delivery results retained in one state snapshot.</summary>
    public const int MaxTerminalDeliveryEvidenceItems = 256;

    /// <summary>Gets the maximum number of structured validation errors returned at one boundary.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum canonical schedule document size.</summary>
    public const int MaxCanonicalDocumentUtf8Bytes = 256 * 1024;

    /// <summary>Gets the number of lowercase hexadecimal characters in a SHA-256 hash.</summary>
    public const int Sha256HexCharacters = 64;

    /// <summary>Gets the earliest local or UTC year admitted by the POC schedule contract.</summary>
    public const int MinimumSupportedYear = 2000;

    /// <summary>Gets the latest local or UTC year admitted without arithmetic overflow.</summary>
    public const int MaximumSupportedYear = 9998;
}
