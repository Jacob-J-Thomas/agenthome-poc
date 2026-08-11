using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialLegacyDefinitionProjectorTests
{
    [Fact]
    public void Projection_is_deterministic_and_preserves_exact_plan_order_instructions_and_fixed_policies()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(
            2,
            ["infer-b", "infer-a"]);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var invocation = Invocation(includeConversation: true);
        var binding = Binding(artifact, invocation);

        var first = GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact);
        var second = GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact);

        Assert.Equal(GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready, first.Status);
        var definition = Assert.IsType<CustomLoopDefinition>(first.Definition);
        Assert.Equal(definition.ContentHash, Assert.IsType<CustomLoopDefinition>(second.Definition).ContentHash);
        Assert.Equal(1, definition.SchemaVersion);
        Assert.Equal(1, definition.DefinitionVersion);
        Assert.Equal(artifact.Graph.GraphId, definition.Id);
        Assert.Equal(artifact.RevisionArtifact.CreatedAtUtc, definition.CreatedAtUtc);
        Assert.Equal(definition.CreatedAtUtc, definition.UpdatedAtUtc);
        Assert.Equal(binding.AdmissionOperationId, definition.LastMutationOperationId);
        Assert.Equal(artifact.Graph.DisplayMetadata.DisplayName, definition.DisplayName);
        Assert.Equal(artifact.Graph.DisplayMetadata.Description, definition.Description);
        Assert.Equal(artifact.Graph.OwningRole.Identity.RoleId, definition.RoleId);
        Assert.Equal(
            ["infer-b", "infer-a"],
            definition.InferenceSteps.Select(step => step.Id));
        Assert.Equal(
            ["Execute bounded inference step 1.", "Execute bounded inference step 2."],
            definition.InferenceSteps.Select(step => step.Instruction));
        Assert.All(definition.InferenceSteps, step => Assert.Equal(CustomLoopNodeContextPolicy.Inherit(), step.ContextPolicy));
        Assert.Equal(new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, true), definition.TriggerPolicy);
        Assert.Equal(CustomLoopContextDefaults.CreatePrototypeDefaults(), definition.ContextDefaults);
        Assert.Empty(definition.ToolAssignments);
        Assert.Equal(new CustomLoopExitPolicy(0, CustomLoopDefinition.DefaultExitDecisionInstruction, CustomLoopNodeContextPolicy.Inherit()), definition.ExitPolicy);
        Assert.Equal(
            [LoopCapabilityRequirements.ConversationTurnId],
            LoopCapabilityRequirements.GetAssignedCapabilityIds(definition.CapabilityRequirements));
        Assert.True(CustomLoopDefinitionContentHash.Matches(definition));
        Assert.True(CustomLoopDefinitionValidator.Validate(definition).IsValid);
    }

    [Fact]
    public void Projection_rejects_binding_invocation_artifact_and_plan_substitution()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact();
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var invocation = Invocation(includeConversation: true);
        var binding = Binding(artifact, invocation);
        var otherArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2);
        var otherPlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(otherArtifact).Plan);

        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidBinding,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(binding with { ContentHash = Hash('f') }, invocation, plan, artifact).Status);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidBinding,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation with { ContentHash = Hash('f') }, plan, artifact).Status);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidArtifact,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(Rehash(binding with { GraphArtifactHash = Hash('e') }), invocation, plan, artifact).Status);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidPlan,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, otherPlan, artifact).Status);
        Assert.Null(GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, otherPlan, artifact).Definition);
    }

    [Fact]
    public void Exact_layout_substitution_requires_a_new_binding_and_projects_only_display_fields()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact();
        var invocation = Invocation(includeConversation: false);
        var originalBinding = Binding(artifact, invocation);
        var graph = artifact.Graph;
        var changedGraph = GovernedLoopGraphDefinition.Create(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRole,
            graph.EntryNodeId,
            graph.TerminalNodeIds,
            graph.AuthorityCeiling,
            graph.ValueSchemas,
            graph.Nodes,
            graph.ControlEdges,
            graph.Bindings,
            graph.OutputContract,
            new GovernedLoopDisplayMetadata(
                "Changed sequential display",
                "Changed display-only description.",
                graph.DisplayMetadata.Nodes.Select(node => node with
                {
                    DisplayName = node.NodeId == "infer-01" ? "Changed inference label" : node.DisplayName,
                    CanvasX = (node.CanvasX ?? 0) + 25,
                }).ToArray()));
        var changedArtifact = GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            artifact.RevisionArtifact,
            changedGraph);
        var changedPlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(changedArtifact).Plan);

        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidArtifact,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(originalBinding, invocation, changedPlan, changedArtifact).Status);

        var changedBinding = Rehash(originalBinding with
        {
            GraphArtifactHash = changedArtifact.ArtifactHash,
            GraphLayoutHash = changedArtifact.LayoutHash,
        });
        var projected = GovernedLoopSequentialLegacyDefinitionProjector.Project(changedBinding, invocation, changedPlan, changedArtifact);

        Assert.Equal(GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready, projected.Status);
        var definition = Assert.IsType<CustomLoopDefinition>(projected.Definition);
        Assert.Equal("Changed sequential display", definition.DisplayName);
        Assert.Equal("Changed display-only description.", definition.Description);
        Assert.Equal("Changed inference label", Assert.Single(definition.InferenceSteps).Name);
        Assert.False(definition.TriggerPolicy.IncludeInvokingConversation);
        Assert.Equal(
            artifact.Graph.Nodes.Single(node => node.Id == "infer-01").Parameters["instruction"],
            Assert.Single(definition.InferenceSteps).Instruction);
    }

    [Fact]
    public void Tool_enabled_projection_maps_only_the_exact_fenced_legacy_workspace_assignments()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(allowWorkspaceTools: true);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var invocation = Invocation(includeConversation: true);
        var binding = Binding(artifact, invocation);

        var result = GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact);

        Assert.Equal(GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready, result.Status);
        var definition = Assert.IsType<CustomLoopDefinition>(result.Definition);
        Assert.Equal(
            [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search],
            definition.ToolAssignments);
        Assert.Equal(
            [LoopCapabilityRequirements.ConversationTurnId, LoopCapabilityRequirements.WorkspaceCommandId],
            LoopCapabilityRequirements.GetAssignedCapabilityIds(definition.CapabilityRequirements));
        Assert.True(CustomLoopDefinitionValidator.Validate(definition).IsValid);

        var toolFreeArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact();
        var toolFreePlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(toolFreeArtifact).Plan);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidArtifact,
            GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, toolFreePlan, toolFreeArtifact).Status);
    }

    [Fact]
    public void Prepared_projection_matches_the_later_exact_bound_projection_without_a_run_identity()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(allowWorkspaceTools: true);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var invocation = Invocation(includeConversation: false);
        var binding = Binding(artifact, invocation);

        var prepared = GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared(
            binding.AdmissionOperationId,
            invocation,
            plan,
            artifact);
        var bound = GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact);

        Assert.Equal(GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready, prepared.Status);
        Assert.Equal(bound.Definition?.ContentHash, prepared.Definition?.ContentHash);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidBinding,
            GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared("BAD OPERATION", invocation, plan, artifact).Status);
        Assert.Equal(
            GovernedLoopSequentialLegacyDefinitionProjectionStatus.InvalidPlan,
            GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared(binding.AdmissionOperationId, invocation, null, artifact).Status);
    }

    private static GovernedLoopSequentialInvocationSnapshot Invocation(bool includeConversation)
    {
        var context = CustomLoopContextSnapshot.CreateEmpty(GovernedLoopSequentialApplicationTestFixture.Now);
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Execute the exact admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            includeConversation
                ? new CustomLoopConversationReference(
                    "conversation-1",
                    "version-1",
                    GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(-1))
                : null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
    }

    private static GovernedLoopSequentialAdapterBinding Binding(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialInvocationSnapshot invocation)
        => GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            "workspace-sha256:" + Hash('a'),
            GovernedLoopExecutionBinding.Create(1, "run-sequential", artifact.RevisionArtifact.Revision, 1),
            "admit-sequential",
            Hash('b'),
            Hash('c'),
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));

    private static GovernedLoopSequentialAdapterBinding Rehash(GovernedLoopSequentialAdapterBinding binding)
        => GovernedLoopSequentialContractHash.Apply(binding with { ContentHash = string.Empty });

    private static string Hash(char value) => new(value, 64);
}
