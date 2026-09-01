using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using CommonDecisionOperationDisposition = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionOperationDisposition;
using StartupDecisionKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionKind;
using StartupDecisionOperationDisposition = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionOperationDisposition;

namespace EmbodySense.E2EBrowserHost;

internal sealed class DeterministicHumanReviewRuntime : IWebHumanReviewRuntime, IDisposable
{
    private readonly WebAgentRuntimeHost _inner;
    private readonly CustomLoopRunStore _runs;
    private readonly HumanReviewDecisionService _decisions;

    public DeterministicHumanReviewRuntime(WebAgentRuntimeHost inner, string workspaceRoot, DateTimeOffset utcNow)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        var clock = new DeterministicHumanReviewClock(utcNow);
        _runs = new CustomLoopRunStore(new WorkspacePaths(workspaceRoot), clock);
        _decisions = new HumanReviewDecisionService(_runs, new DeterministicHumanReviewDecisionAuthorizer(), clock);
    }

    public bool IsWorkspaceInitialized => _inner.IsWorkspaceInitialized;

    public Task<HumanReviewPage> ListHumanReviewsAsync(HumanReviewPageRequest request, CancellationToken cancellationToken = default)
        => _inner.ListHumanReviewsAsync(request, cancellationToken);

    public Task<HumanReviewReadResult> ReadHumanReviewAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.ReadHumanReviewAsync(runId, cancellationToken);

    public Task<HumanReviewEvidenceReadResult> ReadHumanReviewEvidenceAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.ReadHumanReviewEvidenceAsync(runId, cancellationToken);

    public Task<HumanReviewRuntimePostureReadResult> ReadHumanReviewPostureAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.ReadHumanReviewPostureAsync(runId, cancellationToken);

    public async Task<HumanReviewDecisionResult> DecideHumanReviewAsync(HumanReviewDecisionOperationInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryMapDecisionKind(input.Kind, out var kind))
            {
                return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Invalid, input.DecisionOperationId, null);
            }

            var result = await _decisions.DecideAsync(new HumanReviewDecisionCommand(input.RunId, input.ExpectedLifecycleVersion, input.DecisionOperationId, kind, input.Detail), cancellationToken).ConfigureAwait(false);
            return new HumanReviewDecisionResult(MapDecisionStatus(result.Status), input.DecisionOperationId, MapDecisionEvidence(result.Receipt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Unavailable, input.DecisionOperationId, null);
        }
    }

    public void Dispose() => _runs.Dispose();

    private static bool TryMapDecisionKind(StartupDecisionKind source, out CommonDecisionKind mapped)
    {
        mapped = source switch
        {
            StartupDecisionKind.Approve => CommonDecisionKind.Approve,
            StartupDecisionKind.Reject => CommonDecisionKind.Reject,
            StartupDecisionKind.Cancel => CommonDecisionKind.Cancel,
            StartupDecisionKind.RequestInformation => CommonDecisionKind.RequestInformation,
            _ => CommonDecisionKind.Unknown,
        };
        return mapped != CommonDecisionKind.Unknown;
    }

    private static HumanReviewDecisionStatus MapDecisionStatus(HumanReviewDecisionServiceStatus status)
        => status switch
        {
            HumanReviewDecisionServiceStatus.Accepted => HumanReviewDecisionStatus.Accepted,
            HumanReviewDecisionServiceStatus.InformationRequested => HumanReviewDecisionStatus.InformationRequested,
            HumanReviewDecisionServiceStatus.Denied => HumanReviewDecisionStatus.Denied,
            HumanReviewDecisionServiceStatus.Conflict => HumanReviewDecisionStatus.Conflict,
            HumanReviewDecisionServiceStatus.Expired => HumanReviewDecisionStatus.Expired,
            HumanReviewDecisionServiceStatus.Replayed => HumanReviewDecisionStatus.Replayed,
            HumanReviewDecisionServiceStatus.NotFound => HumanReviewDecisionStatus.NotFound,
            HumanReviewDecisionServiceStatus.Invalid => HumanReviewDecisionStatus.Invalid,
            HumanReviewDecisionServiceStatus.LimitExceeded => HumanReviewDecisionStatus.LimitExceeded,
            _ => HumanReviewDecisionStatus.Unavailable,
        };

    private static HumanReviewDecisionEvidence? MapDecisionEvidence(HumanReviewDecisionOperationReceipt? receipt)
        => receipt is null
            ? null
            : new HumanReviewDecisionEvidence(receipt.DecisionOperationId, receipt.Request.RequestId, MapDecisionOperationDisposition(receipt.Disposition), receipt.Decision is null ? null : MapDecisionKind(receipt.Decision.Kind), receipt.RecordedAtUtc, receipt.ProposalHash, receipt.ReceiptHash);

    private static StartupDecisionOperationDisposition MapDecisionOperationDisposition(CommonDecisionOperationDisposition source)
        => source switch
        {
            CommonDecisionOperationDisposition.Accepted => StartupDecisionOperationDisposition.Accepted,
            CommonDecisionOperationDisposition.InformationRequested => StartupDecisionOperationDisposition.InformationRequested,
            CommonDecisionOperationDisposition.Denied => StartupDecisionOperationDisposition.Denied,
            CommonDecisionOperationDisposition.Conflict => StartupDecisionOperationDisposition.Conflict,
            CommonDecisionOperationDisposition.Expired => StartupDecisionOperationDisposition.Expired,
            _ => StartupDecisionOperationDisposition.Unknown,
        };

    private static StartupDecisionKind MapDecisionKind(CommonDecisionKind source)
        => source switch
        {
            CommonDecisionKind.Approve => StartupDecisionKind.Approve,
            CommonDecisionKind.Reject => StartupDecisionKind.Reject,
            CommonDecisionKind.Cancel => StartupDecisionKind.Cancel,
            CommonDecisionKind.RequestInformation => StartupDecisionKind.RequestInformation,
            _ => StartupDecisionKind.Unknown,
        };
}
