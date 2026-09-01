using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

internal sealed class HumanReviewControllerTestRuntime : IWebHumanReviewRuntime
{
    public bool IsWorkspaceInitialized { get; set; } = true;

    public HumanReviewPage? PageResponse { get; set; } = new(HumanReviewPageStatus.Ready, [], null);

    public HumanReviewReadResult? ReadResponse { get; set; } = new(HumanReviewReadStatus.NotFound);

    public HumanReviewEvidenceReadResult? EvidenceResponse { get; set; } = new(HumanReviewEvidenceReadStatus.NotFound, [], null);

    public HumanReviewRuntimePostureReadResult? PostureResponse { get; set; } = new(HumanReviewReadStatus.NotFound, null);

    public HumanReviewDecisionResult? DecisionResponse { get; set; } = new(HumanReviewDecisionStatus.Invalid, string.Empty, null);

    public Exception? PageException { get; set; }

    public Exception? ReadException { get; set; }

    public Exception? EvidenceException { get; set; }

    public Exception? PostureException { get; set; }

    public Exception? DecisionException { get; set; }

    public int ListCalls { get; private set; }

    public int ReadCalls { get; private set; }

    public int EvidenceCalls { get; private set; }

    public int PostureCalls { get; private set; }

    public int DecisionCalls { get; private set; }

    public HumanReviewDecisionOperationInput? LastDecision { get; private set; }

    public Task<HumanReviewPage> ListHumanReviewsAsync(HumanReviewPageRequest request, CancellationToken cancellationToken = default)
    {
        ListCalls++;
        ThrowIfConfigured(PageException);
        return Task.FromResult(PageResponse!);
    }

    public Task<HumanReviewReadResult> ReadHumanReviewAsync(string runId, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        ThrowIfConfigured(ReadException);
        return Task.FromResult(ReadResponse!);
    }

    public Task<HumanReviewEvidenceReadResult> ReadHumanReviewEvidenceAsync(string runId, CancellationToken cancellationToken = default)
    {
        EvidenceCalls++;
        ThrowIfConfigured(EvidenceException);
        return Task.FromResult(EvidenceResponse!);
    }

    public Task<HumanReviewRuntimePostureReadResult> ReadHumanReviewPostureAsync(string runId, CancellationToken cancellationToken = default)
    {
        PostureCalls++;
        ThrowIfConfigured(PostureException);
        return Task.FromResult(PostureResponse!);
    }

    public Task<HumanReviewDecisionResult> DecideHumanReviewAsync(HumanReviewDecisionOperationInput input, CancellationToken cancellationToken = default)
    {
        DecisionCalls++;
        LastDecision = input;
        ThrowIfConfigured(DecisionException);
        return Task.FromResult(DecisionResponse!);
    }

    private static void ThrowIfConfigured(Exception? exception)
    {
        if (exception is not null)
        {
            throw exception;
        }
    }
}
