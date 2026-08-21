using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Projects one attempt-local ToolBroker mutation into the canonical #338 effect-attempt service.</summary>
public sealed class GovernedWorkspaceMutationToolExecutor : IGovernedWorkspaceMutationToolExecutor
{
    private readonly GovernedLoopAdmissionReceipt _admission;
    private readonly string _correlationId;
    private readonly GovernedLoopExecutionBinding _execution;
    private readonly GovernedLoopGraphRevisionArtifact _graph;
    private readonly string _nodeId;
    private readonly int _nodeAttempt;
    private readonly CapabilityAdmissionPin _pin;
    private readonly AuthorityCeiling _requiredAuthority;
    private readonly GovernedLoopEffectAttemptFacade _facade;

    /// <summary>Creates one exact admitted run/node projection.</summary>
    public GovernedWorkspaceMutationToolExecutor(
        GovernedLoopEffectAttemptFacade facade,
        GovernedLoopAdmissionReceipt admission,
        GovernedLoopExecutionBinding execution,
        GovernedLoopGraphRevisionArtifact graph,
        string nodeId,
        int nodeAttempt,
        string correlationId,
        CapabilityAdmissionPin pin)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _pin = pin ?? throw new ArgumentNullException(nameof(pin));
        _requiredAuthority = ValidateAndCreateRequiredAuthority(admission, execution, graph, nodeId, nodeAttempt, correlationId, pin);
        _nodeId = nodeId;
        _nodeAttempt = nodeAttempt;
        _correlationId = correlationId;
    }

    /// <inheritdoc />
    public async Task<LocalWorkspaceResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteEffectAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Status is not (GovernedLoopEffectAttemptExecutionStatus.Committed or GovernedLoopEffectAttemptExecutionStatus.Replayed)
            || result.Attempt?.AfterEvidenceId is not { } afterEvidenceId)
        {
            throw new IOException($"Governed workspace mutation stopped with explicit posture `{result.Status}`.");
        }
        return new LocalWorkspaceResult(
            result.Status == GovernedLoopEffectAttemptExecutionStatus.Replayed
                ? "governed workspace outcome replayed"
                : "governed workspace outcome committed",
            new Dictionary<string, object?>
            {
                ["effect_status"] = result.Status.ToString(),
                ["after_evidence_id"] = afterEvidenceId,
                ["effect_generation"] = 1,
            });
    }

    internal async Task<GovernedLoopEffectAttemptExecutionResult> ExecuteEffectAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Command switch
        {
            ToolCommand.Append => WorkspaceActionKind.Append,
            ToolCommand.Write => WorkspaceActionKind.Write,
            ToolCommand.Delete => WorkspaceActionKind.Delete,
            _ => WorkspaceActionKind.Unknown,
        };
        string? reasonCode = null;
        if (kind == WorkspaceActionKind.Unknown
            || !WorkspaceActionInputContract.TryParse(request.Content, kind, out var input, out reasonCode)
            || !string.Equals(request.TargetPath, input!.Target.Value, StringComparison.Ordinal)
            || !string.Equals(input.ScopeId.Value, "workspace", StringComparison.Ordinal))
        {
            throw new IOException($"Governed workspace semantic input is invalid ({reasonCode ?? "workspace-tool-target-mismatch"}).");
        }

        var canonical = WorkspaceActionInputContract.Encode(input);
        var identity = WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-tool-effect.v1",
            _execution.RunId,
            _nodeId,
            _nodeAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _correlationId,
            request.CorrelationId,
            WorkspaceActionOperationIds.For(kind),
            canonical);
        return await _facade.ExecuteAsync(
            new GovernedLoopEffectAttemptRequest(
                _admission,
                _execution,
                _graph,
                _nodeId,
                _nodeAttempt,
                _pin,
                WorkspaceActionOperationIds.For(kind),
                "effect-" + identity,
                "operation-" + identity,
                1,
                canonical,
                _requiredAuthority,
                _correlationId),
            cancellationToken).ConfigureAwait(false);
    }

    private static AuthorityCeiling ValidateAndCreateRequiredAuthority(
        GovernedLoopAdmissionReceipt admission,
        GovernedLoopExecutionBinding execution,
        GovernedLoopGraphRevisionArtifact graph,
        string nodeId,
        int nodeAttempt,
        string correlationId,
        CapabilityAdmissionPin pin)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(nodeId, GovernedLoopExecutionLimits.MaxIdentifierCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(correlationId, GovernedLoopExecutionLimits.MaxIdentifierCharacters)
            || nodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || !GovernedLoopAdmissionValidator.Validate(admission).IsValid
            || !Equals(execution, admission.Evidence.Binding)
            || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(graph), graph.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(graph.ArtifactHash, admission.Intent.GraphArtifactHash, StringComparison.Ordinal)
            || !Equals(graph.RevisionArtifact.Revision, execution.Revision)
            || !admission.Evidence.CapabilityAdmission.Pins.Contains(pin)
            || !admission.Evidence.EffectiveAuthority.Capabilities.Contains(pin.DescriptorIdentity))
        {
            throw new ArgumentException("The workspace action projection requires one exact, bounded admitted run and capability pin.", nameof(admission));
        }

        var node = graph.Graph.Nodes.SingleOrDefault(candidate => string.Equals(candidate.Id, nodeId, StringComparison.Ordinal));
        if (node is null
            || node.Descriptor.Kind != GovernedLoopNodeKind.Action
            || !node.AuthorityCeiling.CapabilityIds.Contains(pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal))
        {
            throw new ArgumentException("The workspace action projection requires an admitted Action node with the exact workspace capability.", nameof(nodeId));
        }

        var capability = BuiltInCapabilityCatalog.Descriptors.Single(candidate =>
            string.Equals(candidate.Id.Value, "org.embodysense/workspace-command", StringComparison.Ordinal));
        if (!CapabilityDescriptorIdentity.TryCreate(capability, out var identity, out _)
            || !Equals(identity, pin.DescriptorIdentity))
        {
            throw new ArgumentException("The workspace action projection requires the exact current built-in workspace capability pin.", nameof(pin));
        }

        var required = new AuthorityCeiling(
            [pin.DescriptorIdentity],
            capability.Requirements.DataClasses,
            1,
            capability.SideEffectClass,
            false,
            capability.SideEffectClass is CapabilitySideEffectClass.ExternalReversible or CapabilitySideEffectClass.Irreversible,
            capability.SideEffectClass == CapabilitySideEffectClass.Irreversible);
        if (!AuthorityProfileValidator.ValidateCeiling(required).IsValid
            || !(AuthorityCeilingSubset.IsEqual(required, admission.Evidence.EffectiveAuthority)
                || AuthorityCeilingSubset.IsStrictSubset(required, admission.Evidence.EffectiveAuthority)))
        {
            throw new ArgumentException("The server-derived workspace action authority is not admitted by the retained run.", nameof(admission));
        }
        return required;
    }
}
