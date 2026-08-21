using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Startup.Loops.Posture.Models;

namespace EmbodySense.Core.Startup.Loops.Posture;

/// <summary>Exposes the shared bounded operational plane without owning alternate runtime state or policy.</summary>
/// <remarks>Workspace, actor, and surface authority are composition-retained. Adapters provide only operation identity and exact posture evidence; Application owns authorization, optimistic concurrency, batch bounds, reconciliation, and outcomes.</remarks>
public sealed class GovernedLoopOperationalFacade
{
    private readonly string _actorId;
    private readonly IGovernedLoopOperationalController _controls;
    private readonly IGovernedLoopOperationalPostureReader _posture;
    private readonly string _surfaceId;
    private readonly string _workspaceId;

    internal GovernedLoopOperationalFacade(
        string workspaceId,
        string actorId,
        string surfaceId,
        IGovernedLoopOperationalPostureReader posture,
        IGovernedLoopOperationalController controls)
    {
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _actorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        _surfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        _posture = posture ?? throw new ArgumentNullException(nameof(posture));
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));
    }

    /// <summary>Reads finite independent pages from every authoritative local operational family.</summary>
    public Task<GovernedLoopOperationalPostureResult> ReadAsync(
        GovernedLoopOperationalPostureQuery query,
        CancellationToken cancellationToken = default)
        => _posture.ReadAsync(query, cancellationToken);

    /// <summary>Reads the default finite first page from every authoritative local operational family.</summary>
    public Task<GovernedLoopOperationalPostureResult> ReadAsync(CancellationToken cancellationToken = default)
        => ReadAsync(new GovernedLoopOperationalPostureQuery(50, 50, 50, 50), cancellationToken);

    /// <summary>Executes one typed, authority-bound, optimistic, idempotent operational control.</summary>
    public Task<GovernedLoopOperationalControlResult> ControlAsync(
        LoopOperationalControlInput input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Task.FromResult(new GovernedLoopOperationalControlResult(
                GovernedLoopOperationalControlStatus.Invalid,
                string.Empty,
                default,
                string.Empty,
                "operational-control-input-required",
                null,
                null,
                null,
                0,
                0,
                0));
        }

        return _controls.ExecuteAsync(
            new GovernedLoopOperationalControlRequest(
                GovernedLoopOperationalControlRequest.CurrentSchemaVersion,
                _workspaceId,
                input.OperationId,
                input.Kind,
                input.TargetId,
                input.ExpectedRevision,
                input.ExpectedEvidenceHash,
                input.ExpectedAuthorityEvidenceHash,
                _actorId,
                _surfaceId,
                input.MaximumBatchItems),
            cancellationToken);
    }
}
