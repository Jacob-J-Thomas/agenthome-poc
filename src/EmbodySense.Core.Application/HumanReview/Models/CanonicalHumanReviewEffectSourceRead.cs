using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

internal sealed record CanonicalHumanReviewEffectSourceRead(GovernedLoopEffectAttemptReadStatus Status, CustomLoopRunRecord? Run, GovernedLoopEffectAttempt? Attempt);
