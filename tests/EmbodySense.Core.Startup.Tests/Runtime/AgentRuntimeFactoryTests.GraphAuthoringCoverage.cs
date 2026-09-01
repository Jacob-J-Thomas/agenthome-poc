using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_graph_authoring_rejects_malformed_mutations_and_missing_optimistic_targets_without_hiding_current_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var revision = GovernedLoopRevisionReference.Create(1, candidate.GraphId!, candidate.RevisionId!, new string('a', 64));

        var missingInput = await runtime.GovernedLoopGraphAuthoring.MutateAsync(null);
        var unknownKind = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "graph-authoring-unknown-kind",
            (GovernedLoopGraphMutationKind)int.MaxValue,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            null));
        var malformedCandidate = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "graph-authoring-malformed-candidate",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate with { SchemaVersion = int.MaxValue }));
        var mismatchedCandidate = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "graph-authoring-mismatched-candidate",
            GovernedLoopGraphMutationKind.CreateDraft,
            "graph-authoring-route-mismatch",
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var unexpectedCandidate = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "graph-authoring-unexpected-candidate",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Draft,
            1,
            revision,
            null,
            candidate));
        var missingTarget = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "graph-authoring-missing-target",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Draft,
            1,
            null,
            null,
            null));

        AssertInvalidMutation(missingInput, "graph-mutation-kind-invalid");
        AssertInvalidMutation(unknownKind, "graph-mutation-kind-invalid");
        Assert.Equal("invalid", malformedCandidate.Status);
        Assert.NotEmpty(malformedCandidate.Errors);
        Assert.All(malformedCandidate.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Code)));
        AssertInvalidMutation(mismatchedCandidate, "graph-id-mismatch");
        AssertInvalidMutation(unexpectedCandidate, "graph-candidate-unexpected");
        Assert.Equal("unavailable", missingTarget.Status);
        Assert.Equal("graph-authoring-missing-target", missingTarget.OperationId);
        Assert.Equal("unknown", missingTarget.ChangeKind);
        Assert.Empty(missingTarget.Errors);
        Assert.Equal("not-found", missingTarget.Current?.Status);
        Assert.Equal("invalid", (await runtime.GovernedLoopGraphAuthoring.ReadAsync(" ")).Status);
    }

    [Fact]
    public async Task Public_graph_authoring_retry_preview_fails_closed_when_finite_bound_arithmetic_is_invalid()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var result = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "graph-authoring-overflow-retry",
            "inference",
            ["retryable-no-effect"],
            [],
            int.MaxValue,
            long.MaxValue,
            long.MaxValue,
            "fixed",
            long.MaxValue,
            long.MaxValue,
            "deterministic-bounded",
            long.MaxValue,
            long.MaxValue,
            int.MaxValue,
            long.MaxValue,
            "USD",
            int.MaxValue));

        Assert.Equal("invalid", result.Status);
        Assert.Equal("retry-policy-bounds-invalid", result.Reason);
        Assert.Null(result.Policy);
        Assert.Null(result.Preview);
    }

    [Fact]
    public void Public_graph_authoring_selects_only_the_exact_revision_governing_each_mutation()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-authoring-selection", "revision-1", new string('a', 64));
        var publication = new GovernedLoopRevisionPublicationPin(1, revision, "graph-authoring-selection-publication", new string('b', 64));

        Assert.Null(EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(null));
        Assert.Equal(revision, EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(Mutation(GovernedLoopGraphMutationKind.ReplaceDraft, revision, publication)));
        Assert.Equal(revision, EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(Mutation(GovernedLoopGraphMutationKind.Publish, revision, publication)));
        Assert.Equal(revision, EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(Mutation(GovernedLoopGraphMutationKind.Disable, revision, publication)));
        Assert.Equal(revision, EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(Mutation(GovernedLoopGraphMutationKind.Archive, revision, publication)));
        Assert.Null(EmbodySense.Core.Startup.Loops.GraphAuthoring.GovernedLoopGraphAuthoringFacade.SelectTargetRevision(Mutation(GovernedLoopGraphMutationKind.CreateDraft, revision, publication)));

        static GovernedLoopGraphMutationInput Mutation(
            GovernedLoopGraphMutationKind kind,
            GovernedLoopRevisionReference revision,
            GovernedLoopRevisionPublicationPin publication)
            => new("graph-authoring-selection-operation", kind, revision.GraphId, GovernedLoopRevisionLifecycleStatus.Draft, 1, revision, publication, null);
    }

    private static void AssertInvalidMutation(GovernedLoopGraphMutationResponse response, string errorCode)
    {
        Assert.Equal("invalid", response.Status);
        Assert.Equal("unknown", response.ChangeKind);
        Assert.Null(response.Current);
        Assert.Contains(response.Errors, error => string.Equals(error.Code, errorCode, StringComparison.Ordinal));
    }
}
