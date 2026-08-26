using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed record HumanReviewDecisionTestFixture(CustomLoopRunRecord Run, HumanReviewRequest Request);
