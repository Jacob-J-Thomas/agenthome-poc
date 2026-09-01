using System.Reflection;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Startup.HumanReview;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal static class HumanReviewRuntimeFacadeTestFactory
{
    public static HumanReviewRuntimeFacade Create(
        ICustomLoopRunStore runs,
        IHumanReviewDecisionService decisions,
        IHumanReviewCurrentEffectAttemptEvidenceSource? effectEvidence = null,
        IGovernedLoopEffectCertaintySnapshotSource? effectCertainty = null)
    {
        var constructor = typeof(HumanReviewRuntimeFacade).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 4
                    && parameters[0].ParameterType == typeof(ICustomLoopRunStore)
                    && parameters[1].ParameterType == typeof(IHumanReviewDecisionService)
                    && parameters[2].ParameterType == typeof(IHumanReviewCurrentEffectAttemptEvidenceSource)
                    && parameters[3].ParameterType == typeof(IGovernedLoopEffectCertaintySnapshotSource);
            });

        return (HumanReviewRuntimeFacade)constructor.Invoke([runs, decisions, effectEvidence, effectCertainty]);
    }
}
