using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed record HumanReviewContinuationRecoveryPublishedWake(
    CustomLoopRunRecord Run,
    HumanReviewContinuationReservation Reservation,
    HumanReviewContinuationState Continuation);
