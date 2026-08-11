using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>Temporarily projects the pre-boundary revalidation contract while existing custom-loop composition converges.</summary>
internal sealed class LegacyToolActuationAuthorityBoundaryAdapter : IToolActuationAuthorityBoundary
{
    private readonly IToolActuationAuthorityRevalidator _revalidator;

    public LegacyToolActuationAuthorityBoundaryAdapter(IToolActuationAuthorityRevalidator revalidator)
    {
        _revalidator = revalidator ?? throw new ArgumentNullException(nameof(revalidator));
    }

    public async Task<ToolActuationAuthorityExecution> ExecuteAsync<TResult>(ToolRequest request, string resolvedTargetPath, Func<ToolActuationAuthorityExecution, CancellationToken, Task<TResult>> executeActuatorAsync, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedTargetPath);
        ArgumentNullException.ThrowIfNull(executeActuatorAsync);
        var revalidation = await _revalidator.RevalidateAsync(request, cancellationToken);
        ArgumentNullException.ThrowIfNull(revalidation);
        var metadata = new Dictionary<string, object?>(revalidation.AuditMetadata)
        {
            ["authority_phase"] = "pre_actuation_revalidation"
        };
        if (!revalidation.Allowed)
        {
            return new ToolActuationAuthorityExecution(ToolActuationAuthorityDisposition.Denied, revalidation.Detail, metadata);
        }

        var direct = new ToolActuationAuthorityExecution(ToolActuationAuthorityDisposition.Direct, revalidation.Detail, metadata);
        _ = await executeActuatorAsync(direct, cancellationToken);
        return direct;
    }
}
