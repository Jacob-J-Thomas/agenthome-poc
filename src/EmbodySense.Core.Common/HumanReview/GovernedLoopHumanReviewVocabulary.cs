namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Defines the exact schema-1 descriptor vocabulary for the durable governed Human Review gate.</summary>
/// <remarks>The descriptor selects only a server-owned review policy and opaque approval subject. It never carries reviewer identities, eligibility proofs, authorization grants, or deadlines in graph-authored data.</remarks>
public static class GovernedLoopHumanReviewVocabulary
{
    /// <summary>Gets the sole supported durable Human Review gate descriptor identifier.</summary>
    public const string TypeId = "human-review-gate";

    /// <summary>Gets the sole supported durable Human Review gate descriptor version.</summary>
    public const int DescriptorVersion = 1;
}
