namespace EmbodySense.Core.Common.Secrets.Redaction;

/// <summary>
/// Defines hard-bounded limits for one sensitive-value scope and its text projections.
/// </summary>
public sealed record RedactionLimits
{
    /// <summary>Maximum configurable number of supplied sensitive-value entries.</summary>
    public const int AbsoluteMaxSensitiveValues = 64;

    /// <summary>Maximum configurable character count for one sensitive value.</summary>
    public const int AbsoluteMaxSensitiveValueCharacters = 4_096;

    /// <summary>Maximum configurable input or output character count.</summary>
    public const int AbsoluteMaxProjectionCharacters = 262_144;

    /// <summary>Maximum configurable matching work units for one text operation.</summary>
    public const int AbsoluteMaxWorkUnits = 8_000_000;

    /// <summary>Default number of supplied sensitive-value entries accepted by one scope.</summary>
    public const int DefaultMaxSensitiveValues = 32;

    /// <summary>Default character limit for one sensitive value.</summary>
    public const int DefaultMaxSensitiveValueCharacters = 2_048;

    /// <summary>Default input character limit for one text operation.</summary>
    public const int DefaultMaxInputCharacters = 65_536;

    /// <summary>Default output character limit for one text operation.</summary>
    public const int DefaultMaxOutputCharacters = 65_536;

    /// <summary>Default matching work-unit limit for one text operation.</summary>
    public const int DefaultMaxWorkUnits = 2_000_000;

    /// <summary>
    /// Initializes bounded redaction limits.
    /// </summary>
    /// <param name="maxSensitiveValues">Maximum supplied value entries accepted by one scope.</param>
    /// <param name="maxSensitiveValueCharacters">Maximum characters accepted from one sensitive value.</param>
    /// <param name="maxInputCharacters">Maximum input characters inspected by one operation.</param>
    /// <param name="maxOutputCharacters">Maximum projected characters emitted by one operation.</param>
    /// <param name="maxWorkUnits">Maximum bounded pattern checks and character comparisons performed by one operation.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any limit is outside its documented hard bounds.</exception>
    public RedactionLimits(
        int maxSensitiveValues = DefaultMaxSensitiveValues,
        int maxSensitiveValueCharacters = DefaultMaxSensitiveValueCharacters,
        int maxInputCharacters = DefaultMaxInputCharacters,
        int maxOutputCharacters = DefaultMaxOutputCharacters,
        int maxWorkUnits = DefaultMaxWorkUnits)
    {
        MaxSensitiveValues = ValidatePositive(maxSensitiveValues, AbsoluteMaxSensitiveValues, nameof(maxSensitiveValues));
        MaxSensitiveValueCharacters = ValidatePositive(maxSensitiveValueCharacters, AbsoluteMaxSensitiveValueCharacters, nameof(maxSensitiveValueCharacters));
        MaxInputCharacters = ValidatePositive(maxInputCharacters, AbsoluteMaxProjectionCharacters, nameof(maxInputCharacters));
        MaxOutputCharacters = ValidatePositive(maxOutputCharacters, AbsoluteMaxProjectionCharacters, nameof(maxOutputCharacters));
        MaxWorkUnits = ValidatePositive(maxWorkUnits, AbsoluteMaxWorkUnits, nameof(maxWorkUnits));
    }

    /// <summary>Gets the maximum supplied value entries accepted by one scope.</summary>
    public int MaxSensitiveValues { get; }

    /// <summary>Gets the maximum characters accepted from one sensitive value.</summary>
    public int MaxSensitiveValueCharacters { get; }

    /// <summary>Gets the maximum input characters inspected by one operation.</summary>
    public int MaxInputCharacters { get; }

    /// <summary>Gets the maximum projected characters emitted by one operation.</summary>
    public int MaxOutputCharacters { get; }

    /// <summary>Gets the maximum bounded pattern checks and character comparisons performed by one operation.</summary>
    public int MaxWorkUnits { get; }

    private static int ValidatePositive(int value, int maximum, string parameterName)
    {
        if (value <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The limit must be between 1 and {maximum}.");
        }

        return value;
    }
}
