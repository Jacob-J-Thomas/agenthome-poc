using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies one immutable Human Review request and exact proposed ReviewBlocked frontier for one atomic successor admission.</summary>
/// <param name="RunId">The canonical run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The exact canonical run lifecycle version.</param>
/// <param name="Request">The validated immutable Human Review request to retain with the frontier.</param>
/// <param name="BlockedFrontier">The exact validated ReviewBlocked successor frontier to commit with the request.</param>
public sealed record HumanReviewAdmissionCommand(string RunId, int ExpectedLifecycleVersion, HumanReviewRequest Request, GovernedLoopFrontierPosture BlockedFrontier);
