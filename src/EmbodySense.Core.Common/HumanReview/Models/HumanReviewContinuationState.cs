using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines the append-only durable state machine for exactly one continuation wake and generation.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="Wake">The exact immutable published wake.</param>
/// <param name="Claims">The canonical append-only bounded claim history.</param>
/// <param name="Completion">The one terminal completion, if any.</param>
/// <param name="Retirement">The one terminal non-completion retirement, if any.</param>
/// <param name="StateHash">The canonical hash of every behavior-affecting state field.</param>
public sealed record HumanReviewContinuationState(int SchemaVersion, HumanReviewContinuationWake Wake, ImmutableArray<HumanReviewContinuationClaim> Claims, HumanReviewContinuationCompletion? Completion, HumanReviewContinuationRetirement? Retirement, string StateHash)
{
    /// <summary>Gets the only supported continuation-state schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
