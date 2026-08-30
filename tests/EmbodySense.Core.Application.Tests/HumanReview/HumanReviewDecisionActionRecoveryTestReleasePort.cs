using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionActionRecoveryTestReleasePort(HumanReviewDecisionActionReleaseResult result) : IHumanReviewDecisionActionReleasePort
{
    private readonly Dictionary<string, HumanReviewDecisionActionReleaseResult> _results = new(StringComparer.Ordinal);

    public int Count { get; private set; }
    public int IdempotentOperationCount => _results.Count;
    public IReadOnlyCollection<string> ActionOperationIds => _results.Keys;

    public Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
    {
        Count++;
        if (!_results.TryGetValue(intent.ActionOperationId, out var retained))
        {
            retained = result;
            _results.Add(intent.ActionOperationId, retained);
        }

        return Task.FromResult(retained);
    }
}
