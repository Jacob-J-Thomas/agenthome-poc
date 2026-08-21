namespace EmbodySense.Core.Common.Inference.Profiles;

/// <summary>Defines the finite public boundary for one governed provider-attempt payload.</summary>
public static class GovernedModelInferencePayloadLimits
{
    /// <summary>Gets the maximum number of ordered messages in one provider attempt.</summary>
    public const int MaxMessages = 384;

    /// <summary>Gets the maximum number of ordered trusted instruction blocks.</summary>
    public const int MaxTrustedInstructions = 512;

    /// <summary>Gets the maximum number of UTF-16 characters in any one payload string.</summary>
    public const int MaxSegmentCharacters = 256_000;

    /// <summary>Gets the maximum aggregate UTF-16 characters across all caller-controlled payload strings.</summary>
    public const int MaxAggregateCharacters = 256_000;

    /// <summary>Gets the maximum aggregate strict UTF-8 bytes across all caller-controlled payload strings.</summary>
    public const int MaxAggregateUtf8Bytes = MaxAggregateCharacters * 2;
}
