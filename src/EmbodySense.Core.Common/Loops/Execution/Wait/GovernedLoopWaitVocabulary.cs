namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Defines the exact schema-1 descriptor and parameter vocabulary for executable Wait nodes.</summary>
public static class GovernedLoopWaitVocabulary
{
    /// <summary>Gets the only supported Wait descriptor version.</summary>
    public const int DescriptorVersion = 1;

    /// <summary>Gets the descriptor for one exact UTC timestamp wait.</summary>
    public const string Timestamp = "wait-timestamp";

    /// <summary>Gets the descriptor for one authenticated governed-event wait.</summary>
    public const string AuthenticatedEvent = "wait-authenticated-event";

    /// <summary>Gets the sole timestamp descriptor parameter.</summary>
    public const string DeadlineUtcParameter = "deadline-utc";

    /// <summary>Gets the sole authenticated-event descriptor parameter.</summary>
    public const string EventReferenceParameter = "event-reference";

    /// <summary>Gets the exact canonical UTC timestamp format used by Wait descriptor parameters.</summary>
    public const string CanonicalUtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly string[] _descriptorTypeIds = [AuthenticatedEvent, Timestamp];

    /// <summary>Gets the immutable closed Wait descriptor catalog in ordinal order.</summary>
    public static IReadOnlyList<string> DescriptorTypeIds => Array.AsReadOnly(_descriptorTypeIds);

    /// <summary>Gets whether a type identifier belongs to the closed Wait catalog.</summary>
    public static bool IsSupported(string? typeId) => typeId is Timestamp or AuthenticatedEvent;
}
