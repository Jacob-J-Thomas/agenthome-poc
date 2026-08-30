using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies one immutable Human Review request and exact proposed ReviewBlocked frontier for one atomic successor admission.</summary>
/// <param name="RunId">The canonical run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The exact canonical run lifecycle version.</param>
/// <param name="Request">The validated immutable Human Review request to retain with the frontier.</param>
/// <param name="BlockedFrontier">The exact validated ReviewBlocked successor frontier to commit with the request.</param>
/// <param name="ReviewBlockedEvent">The exact node-outcome event proving the Human Review gate was parked before the request could become observable.</param>
public sealed record HumanReviewAdmissionCommand(string RunId, int ExpectedLifecycleVersion, HumanReviewRequest Request, GovernedLoopFrontierPosture BlockedFrontier, CustomLoopRunEvent? ReviewBlockedEvent = null);
