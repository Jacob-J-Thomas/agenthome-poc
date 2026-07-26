using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CustomLoopToolActuationAuthorityRevalidator : IToolActuationAuthorityRevalidator
{
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly CustomLoopInferenceAttemptRequest _attempt;
    private readonly CorrelatedToolEvidenceObserver _observer;

    public CustomLoopToolActuationAuthorityRevalidator(ICustomLoopToolAuthorityProvider authorityProvider, CustomLoopInferenceAttemptRequest attempt, CorrelatedToolEvidenceObserver observer)
    {
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public async Task<ToolActuationAuthorityRevalidation> RevalidateAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authority = await _authorityProvider.ResolveAsync(_attempt.RoleId, _attempt.AdmittedToolAssignments, cancellationToken);
        _observer.RefreshAuthority(request, authority);
        var assignment = MapAssignment(request.Command);
        var allowed = authority.IsValid && assignment is not null && authority.EffectiveAssignments.Contains(assignment.Value);
        var detail = allowed
            ? "Current role and loop authority still allow the approved command at the actuation boundary."
            : "Current role or loop authority revoked the approved command before actuation.";
        var metadata = new Dictionary<string, object?>
        {
            ["current_role_id"] = authority.RoleId,
            ["admitted_commands"] = Join(authority.AdmittedMaximum),
            ["current_role_commands"] = Join(authority.CurrentRoleCeiling),
            ["effective_commands"] = Join(authority.EffectiveAssignments),
            ["role_ceiling_hash"] = authority.RoleCeilingHash,
            ["catalog_hash"] = authority.CatalogHash,
            ["authority_evaluated_at_utc"] = authority.EvaluatedAtUtc,
            ["authority_valid"] = authority.IsValid
        };
        return new ToolActuationAuthorityRevalidation(allowed, authority.IsValid ? detail : authority.Detail, metadata);
    }

    private static CustomLoopToolAssignment? MapAssignment(ToolCommand command)
    {
        return command switch
        {
            ToolCommand.List => CustomLoopToolAssignment.List,
            ToolCommand.Read => CustomLoopToolAssignment.Read,
            ToolCommand.Search => CustomLoopToolAssignment.Search,
            _ => null
        };
    }

    private static string Join(IEnumerable<CustomLoopToolAssignment> assignments)
    {
        return string.Join(',', assignments.OrderBy(value => value).Select(value => value.ToString().ToLowerInvariant()));
    }
}