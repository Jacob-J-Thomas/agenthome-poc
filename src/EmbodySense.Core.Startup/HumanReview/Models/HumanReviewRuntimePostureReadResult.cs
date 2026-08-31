namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns one detached runtime posture read.</summary>
/// <param name="Status">The read outcome.</param>
/// <param name="Posture">The detached posture when available.</param>
public sealed record HumanReviewRuntimePostureReadResult(HumanReviewReadStatus Status, HumanReviewRuntimePosture? Posture);
