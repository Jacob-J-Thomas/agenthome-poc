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

    /// <summary>Reads caller-bounded independent pages without exposing Application ports to an interface adapter.</summary>
    /// <param name="maximumQueueEntries">The queue page bound.</param>
    /// <param name="maximumSchedules">The schedule page bound.</param>
    /// <param name="maximumWakes">The sleeping-checkpoint page bound.</param>
    /// <param name="maximumRuns">The durable-run page bound.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The canonical fail-closed posture result.</returns>
    public Task<GovernedLoopOperationalPostureResult> ReadAsync(
        int maximumQueueEntries,
        int maximumSchedules,
        int maximumWakes,
        int maximumRuns,
        CancellationToken cancellationToken = default)
        => ReadAsync(maximumQueueEntries, maximumSchedules, maximumWakes, maximumRuns, null, null, null, null, cancellationToken);

    /// <summary>Reads caller-bounded independent pages from exact continuation cursors without exposing Application ports.</summary>
    /// <param name="maximumQueueEntries">The queue page bound.</param>
    /// <param name="maximumSchedules">The schedule page bound.</param>
    /// <param name="maximumWakes">The sleeping-checkpoint page bound.</param>
    /// <param name="maximumRuns">The durable-run page bound.</param>
    /// <param name="queueCursor">The opaque queue continuation cursor.</param>
    /// <param name="afterScheduleId">The exclusive schedule cursor.</param>
    /// <param name="afterCheckpointId">The exclusive checkpoint cursor.</param>
    /// <param name="afterRunId">The exclusive run cursor.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The canonical fail-closed posture result.</returns>
    public Task<GovernedLoopOperationalPostureResult> ReadAsync(
        int maximumQueueEntries,
        int maximumSchedules,
        int maximumWakes,
        int maximumRuns,
        string? queueCursor,
        string? afterScheduleId,
        string? afterCheckpointId,
        string? afterRunId,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            new GovernedLoopOperationalPostureQuery(
                maximumQueueEntries,
                maximumSchedules,
                maximumWakes,
                maximumRuns,
                queueCursor,
                afterScheduleId,
                afterCheckpointId,
                afterRunId),
            cancellationToken);

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

    /// <summary>Executes one typed operational control selected by its exact kebab-case public token.</summary>
    /// <param name="operationId">The caller-owned idempotency identity.</param>
    /// <param name="kind">One exact kebab-case <see cref="GovernedLoopOperationalControlKind"/> token.</param>
    /// <param name="targetId">The exact caller-observed target identity.</param>
    /// <param name="expectedRevision">The exact optimistic target revision.</param>
    /// <param name="expectedEvidenceHash">The exact optimistic target evidence hash.</param>
    /// <param name="expectedAuthorityEvidenceHash">The exact authority evidence hash observed with the posture snapshot.</param>
    /// <param name="maximumBatchItems">The explicit bounded batch size, which must be one for non-batch operations.</param>
    /// <param name="cancellationToken">Cancels the operation before its durable integrity boundary.</param>
    /// <returns>The canonical control result; an unknown token returns <see cref="GovernedLoopOperationalControlStatus.Invalid"/> without mutation.</returns>
    /// <remarks>This overload lets interface adapters remain dependent only on Core.Startup while Application retains control selection and policy.</remarks>
    public Task<GovernedLoopOperationalControlResult> ControlAsync(
        string operationId,
        string kind,
        string targetId,
        long expectedRevision,
        string expectedEvidenceHash,
        string expectedAuthorityEvidenceHash,
        int maximumBatchItems = 1,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseKind(kind, out var parsedKind))
        {
            return Task.FromResult(new GovernedLoopOperationalControlResult(
                GovernedLoopOperationalControlStatus.Invalid,
                operationId ?? string.Empty,
                default,
                targetId ?? string.Empty,
                "operational-control-kind-invalid",
                null,
                null,
                null,
                0,
                0,
                0));
        }

        return ControlAsync(
            new LoopOperationalControlInput(
                operationId,
                parsedKind,
                targetId,
                expectedRevision,
                expectedEvidenceHash,
                expectedAuthorityEvidenceHash,
                maximumBatchItems),
            cancellationToken);
    }

    private static bool TryParseKind(string? token, out GovernedLoopOperationalControlKind kind)
    {
        kind = token switch
        {
            "pause-run" => GovernedLoopOperationalControlKind.PauseRun,
            "cancel-run" => GovernedLoopOperationalControlKind.CancelRun,
            "resume-run" => GovernedLoopOperationalControlKind.ResumeRun,
            "disable-schedule" => GovernedLoopOperationalControlKind.DisableSchedule,
            "enable-schedule" => GovernedLoopOperationalControlKind.EnableSchedule,
            "cancel-delivery" => GovernedLoopOperationalControlKind.CancelDelivery,
            "cancel-pending-deliveries" => GovernedLoopOperationalControlKind.CancelPendingDeliveries,
            _ => default,
        };
        return kind != default;
    }
}
