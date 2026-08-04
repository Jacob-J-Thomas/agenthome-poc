namespace EmbodySense.Core.Application.Secrets.Redaction;

/// <summary>
/// Defines hard-bounded traversal limits for structured, header, and exception redaction projections.
/// </summary>
public sealed record RedactionProjectionLimits
{
    /// <summary>Maximum configurable structure or exception depth.</summary>
    public const int AbsoluteMaxDepth = 32;

    /// <summary>Maximum configurable nodes visited by one projection.</summary>
    public const int AbsoluteMaxNodes = 4_096;

    /// <summary>Maximum configurable entries read from one collection.</summary>
    public const int AbsoluteMaxCollectionEntries = 1_024;

    /// <summary>Maximum configurable sanitized characters retained by one aggregate projection.</summary>
    public const int AbsoluteMaxProjectedCharacters = 1_048_576;

    /// <summary>Default maximum structure or exception depth.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>Default maximum nodes visited by one projection.</summary>
    public const int DefaultMaxNodes = 512;

    /// <summary>Default maximum entries read from one collection.</summary>
    public const int DefaultMaxCollectionEntries = 128;

    /// <summary>Default maximum sanitized characters retained by one aggregate projection.</summary>
    public const int DefaultMaxProjectedCharacters = 262_144;

    /// <summary>
    /// Initializes bounded traversal limits.
    /// </summary>
    /// <param name="maxDepth">Maximum nested structure or exception depth.</param>
    /// <param name="maxNodes">Maximum nodes visited in one projection.</param>
    /// <param name="maxCollectionEntries">Maximum entries consumed from one collection.</param>
    /// <param name="maxProjectedCharacters">Maximum sanitized characters retained across the aggregate projection.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a limit is outside its documented hard bounds.</exception>
    public RedactionProjectionLimits(
        int maxDepth = DefaultMaxDepth,
        int maxNodes = DefaultMaxNodes,
        int maxCollectionEntries = DefaultMaxCollectionEntries,
        int maxProjectedCharacters = DefaultMaxProjectedCharacters)
    {
        MaxDepth = ValidatePositive(maxDepth, AbsoluteMaxDepth, nameof(maxDepth));
        MaxNodes = ValidatePositive(maxNodes, AbsoluteMaxNodes, nameof(maxNodes));
        MaxCollectionEntries = ValidatePositive(maxCollectionEntries, AbsoluteMaxCollectionEntries, nameof(maxCollectionEntries));
        MaxProjectedCharacters = ValidatePositive(maxProjectedCharacters, AbsoluteMaxProjectedCharacters, nameof(maxProjectedCharacters));
    }

    /// <summary>Gets the maximum nested structure or exception depth.</summary>
    public int MaxDepth { get; }

    /// <summary>Gets the maximum nodes visited in one projection.</summary>
    public int MaxNodes { get; }

    /// <summary>Gets the maximum entries consumed from one collection.</summary>
    public int MaxCollectionEntries { get; }

    /// <summary>Gets the maximum sanitized characters retained across the aggregate projection.</summary>
    public int MaxProjectedCharacters { get; }

    private static int ValidatePositive(int value, int maximum, string parameterName)
    {
        if (value <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The limit must be between 1 and {maximum}.");
        }

        return value;
    }
}
