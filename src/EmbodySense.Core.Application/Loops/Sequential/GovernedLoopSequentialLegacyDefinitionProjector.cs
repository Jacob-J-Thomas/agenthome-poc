using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Projects one exact first-wave canonical graph into the single fenced ordered-runtime definition shape.</summary>
/// <remarks>
/// The projection is deterministic and carries no authority. It exists only while the established ordered runtime remains
/// the execution engine: canonical admission evidence, capability pins, graph identity, and node identities stay authoritative.
/// </remarks>
public static class GovernedLoopSequentialLegacyDefinitionProjector
{
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    /// <summary>Projects a validated exact binding, invocation snapshot, plan, and immutable graph artifact.</summary>
    public static GovernedLoopSequentialLegacyDefinitionProjectionResult Project(
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialInvocationSnapshot? invocationSnapshot,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (!GovernedLoopSequentialContractValidator.Validate(binding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(invocationSnapshot).IsValid)
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidBinding);
        }

        if (!IsExactArtifact(artifact)
            || !Equals(binding!.ExecutionBinding.Revision, artifact!.RevisionArtifact.Revision)
            || !string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
            || !string.Equals(binding.InvocationPayloadHash, invocationSnapshot!.ContentHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidArtifact);
        }

        if (!IsExactPlan(plan, artifact)
            || !Equals(plan!.Revision, binding.ExecutionBinding.Revision)
            || !string.Equals(plan.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(plan.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidPlan);
        }

        return CreateProjection(binding.AdmissionOperationId, invocationSnapshot, plan, artifact);
    }

    /// <summary>Projects the deterministic compatibility definition before admission assigns the server-owned run identity.</summary>
    /// <remarks>The returned definition carries no admission, authority, or execution grant and exists only to authenticate a pre-admission invocation receipt.</remarks>
    public static GovernedLoopSequentialLegacyDefinitionProjectionResult ProjectPrepared(
        string? admissionOperationId,
        GovernedLoopSequentialInvocationSnapshot? invocationSnapshot,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(admissionOperationId, GovernedLoopSequentialContractLimits.MaxIdentifierCharacters)
            || !GovernedLoopSequentialContractValidator.Validate(invocationSnapshot).IsValid)
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidBinding);
        }

        if (!IsExactArtifact(artifact))
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidArtifact);
        }

        if (!IsExactPlan(plan, artifact!))
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidPlan);
        }

        return CreateProjection(admissionOperationId!, invocationSnapshot!, plan!, artifact!);
    }

    private static GovernedLoopSequentialLegacyDefinitionProjectionResult CreateProjection(
        string admissionOperationId,
        GovernedLoopSequentialInvocationSnapshot invocationSnapshot,
        GovernedLoopSequentialPlan plan,
        GovernedLoopGraphRevisionArtifact artifact)
    {
        try
        {
            var graph = artifact.Graph;
            var graphNodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var displayNodes = graph.DisplayMetadata.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            var inferenceSteps = plan.Nodes
                .Where(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
                .Select(node =>
                {
                    var graphNode = graphNodes[node.NodeId];
                    var instruction = graphNode.Parameters["instruction"];
                    var displayName = displayNodes[node.NodeId].DisplayName;
                    return new CustomLoopInferenceStep(
                        node.NodeId,
                        displayName,
                        instruction,
                        CustomLoopNodeContextPolicy.Inherit());
                })
                .ToArray();
            var createdAtUtc = artifact.RevisionArtifact.CreatedAtUtc.ToUniversalTime();
            CustomLoopToolAssignment[] toolAssignments = graph.Nodes.Any(node => node.Descriptor.Kind == EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Inference
                && node.AuthorityCeiling.CapabilityIds.Contains(WorkspaceCommandCapabilityId, StringComparer.Ordinal))
                ? [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search]
                : [];
            var definition = new CustomLoopDefinition(
                CustomLoopDefinition.CurrentSchemaVersion,
                graph.GraphId,
                1,
                string.Empty,
                createdAtUtc,
                createdAtUtc,
                graph.DisplayMetadata.DisplayName,
                graph.DisplayMetadata.Description,
                graph.OwningRole.Identity.RoleId,
                new CustomLoopTriggerPolicy(
                    CustomLoopTriggerPromptSource.Invocation,
                    string.Empty,
                    invocationSnapshot.InvokingConversation is not null),
                CustomLoopContextDefaults.CreatePrototypeDefaults(),
                inferenceSteps,
                toolAssignments,
                new CustomLoopExitPolicy(
                    0,
                    CustomLoopDefinition.DefaultExitDecisionInstruction,
                    CustomLoopNodeContextPolicy.Inherit()),
                admissionOperationId)
            {
                // This manifest exists only to satisfy the fenced legacy definition contract. The canonical ordered
                // path revalidates the exact graph capability pins supplied by its immutable adapter hand-off.
                CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(graph.GraphId, toolAssignments),
            };
            definition = CustomLoopDefinitionContentHash.Apply(definition);
            return CustomLoopDefinitionValidator.ValidateSequentialProjection(definition).IsValid
                ? new GovernedLoopSequentialLegacyDefinitionProjectionResult(
                    GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready,
                    definition)
                : Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidProjection);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Failure(GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidProjection);
        }
    }

    internal static bool PlansEqual(GovernedLoopSequentialPlan left, GovernedLoopSequentialPlan right)
        => left.SchemaVersion == right.SchemaVersion
            && Equals(left.Revision, right.Revision)
            && string.Equals(left.GraphArtifactHash, right.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(left.GraphLayoutHash, right.GraphLayoutHash, StringComparison.Ordinal)
            && left.Nodes.Count == right.Nodes.Count
            && left.Nodes.Zip(right.Nodes).All(pair => pair.First.Ordinal == pair.Second.Ordinal
                && string.Equals(pair.First.NodeId, pair.Second.NodeId, StringComparison.Ordinal)
                && Equals(pair.First.Descriptor, pair.Second.Descriptor)
                && string.Equals(pair.First.IncomingControlEdgeId, pair.Second.IncomingControlEdgeId, StringComparison.Ordinal)
                && string.Equals(pair.First.OutgoingControlEdgeId, pair.Second.OutgoingControlEdgeId, StringComparison.Ordinal));

    private static bool IsExactPlan(
        GovernedLoopSequentialPlan? plan,
        GovernedLoopGraphRevisionArtifact artifact)
    {
        var rebuilt = GovernedLoopSequentialPlanBuilder.Build(artifact);
        return plan is not null
            && rebuilt.Status == GovernedLoopSequentialPlanBuildStatus.Ready
            && rebuilt.Plan is not null
            && PlansEqual(plan, rebuilt.Plan);
    }

    private static bool IsExactArtifact(GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (artifact is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact),
                artifact.ArtifactHash,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static GovernedLoopSequentialLegacyDefinitionProjectionResult Failure(
        GovernedLoopSequentialLegacyDefinitionProjectionStatus status)
        => new(status, null);
}
