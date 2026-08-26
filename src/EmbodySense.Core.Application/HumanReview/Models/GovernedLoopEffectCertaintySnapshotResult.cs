using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed read-only current-effect-certainty result and, only when exact, its detached value-free snapshot.</summary>
/// <param name="Status">The closed source-read disposition.</param>
/// <param name="Snapshot">The detached current snapshot only when <paramref name="Status"/> is <see cref="GovernedLoopEffectCertaintySnapshotStatus.Current"/>.</param>
public sealed record GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus Status, HumanReviewEffectCertaintySnapshot? Snapshot = null);
