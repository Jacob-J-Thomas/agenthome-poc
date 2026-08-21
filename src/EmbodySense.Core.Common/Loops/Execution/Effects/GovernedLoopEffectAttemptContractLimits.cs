namespace EmbodySense.Core.Common.Loops.Execution.Effects;

/// <summary>Defines finite schema-1 bounds for actuator operations and retained effect attempts.</summary>
public static class GovernedLoopEffectAttemptContractLimits
{
    /// <summary>Gets the only supported contract schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the largest canonical actuator-operation identifier.</summary>
    public const int MaxOperationIdCharacters = 120;

    /// <summary>Gets the largest safe operator-facing risk summary.</summary>
    public const int MaxRiskSummaryCharacters = 512;

    /// <summary>Gets the largest canonical input accepted before hashing.</summary>
    public const int MaxCanonicalInputUtf8Bytes = 32 * 1024;

    /// <summary>Gets the maximum JSON nesting depth accepted for actuator input.</summary>
    public const int MaxInputDepth = 16;

    /// <summary>Gets the maximum number of JSON values and properties accepted for actuator input.</summary>
    public const int MaxInputElements = 512;

    /// <summary>Gets the maximum significant digits accepted in one exact JSON number.</summary>
    public const int MaxInputNumberDigits = 256;

    /// <summary>Gets the largest absolute base-10 exponent accepted in one exact JSON number.</summary>
    public const int MaxInputNumberExponent = 10_000;

    /// <summary>Gets the largest canonical value-free attempt record.</summary>
    public const int MaxRecordUtf8Bytes = 64 * 1024;

    /// <summary>Gets the maximum bounded operator-safe detail length.</summary>
    public const int MaxDetailCharacters = 512;
}
