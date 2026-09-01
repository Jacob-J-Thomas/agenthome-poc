using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Web.Services;

/// <summary>Exposes host-safe Human Review operations through the one retained Web runtime lifetime.</summary>
public interface IWebHumanReviewRuntime
{
    /// <summary>Gets whether the configured workspace is currently initialized.</summary>
    bool IsWorkspaceInitialized { get; }

    /// <summary>Lists one bounded page of detached Human Review summaries.</summary>
    /// <param name="request">The finite page size and opaque continuation cursor.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The canonical detached page result.</returns>
    Task<HumanReviewPage> ListHumanReviewsAsync(HumanReviewPageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact detached Human Review detail projection.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The canonical detached detail result.</returns>
    Task<HumanReviewReadResult> ReadHumanReviewAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact detached Human Review evidence projection.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The canonical detached evidence result.</returns>
    Task<HumanReviewEvidenceReadResult> ReadHumanReviewEvidenceAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact detached Human Review runtime posture.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The canonical detached runtime-posture result.</returns>
    Task<HumanReviewRuntimePostureReadResult> ReadHumanReviewPostureAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Submits one authority-free decision through the retained canonical runtime.</summary>
    /// <param name="input">The route-derived run, version, operation, decision, and optional detail.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the decision operation.</param>
    /// <returns>The detached durable decision result.</returns>
    Task<HumanReviewDecisionResult> DecideHumanReviewAsync(HumanReviewDecisionOperationInput input, CancellationToken cancellationToken = default);
}
