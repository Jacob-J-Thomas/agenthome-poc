using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

public sealed class GovernedWorkspaceActionFactoryTests
{
    [Fact]
    public void Registry_composes_exactly_the_three_server_owned_workspace_actions()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var permissionService = new ToolPermissionService(paths, DirectoryPermissionPolicy.Create(paths, null));
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, new CapabilityAuthorityTransaction(paths), permissionService);

        Assert.Equal(
            [WorkspaceActionOperationIds.Append, WorkspaceActionOperationIds.Delete, WorkspaceActionOperationIds.Write],
            registry.Descriptors.Select(descriptor => descriptor.OperationId).Order(StringComparer.Ordinal));
        Assert.All(registry.Descriptors, descriptor =>
        {
            Assert.Equal("org.embodysense/workspace-command", descriptor.Capability.Id.Value);
            Assert.Equal(GovernedActuatorTargetSemantics.ExactWorkspaceTarget, descriptor.TargetSemantics);
            Assert.True(descriptor.RequiresOptimisticPrecondition);
            Assert.True(descriptor.RequiresBeforeEvidence);
            Assert.True(descriptor.RequiresAfterEvidence);
            Assert.True(descriptor.RequiresOutcomeEvidence);
            Assert.True(descriptor.UnattendedEligible);
        });
    }

    [Fact]
    public async Task Graph_projection_commits_and_replays_one_exact_workspace_action_through_the_production_effect_store()
    {
        using var workspace = new TestWorkspace();
        var fixture = WorkspaceToolAuthorityTestFixture.CreateAction();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        Directory.CreateDirectory(workspace.File("shared"));
        var permissions = new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission
                {
                    Path = ".",
                    Operations = [FileSystemOperation.Create, FileSystemOperation.Append, FileSystemOperation.Modify, FileSystemOperation.Delete],
                    RequiresApproval = false,
                },
            ],
        };
        var transaction = new CapabilityAuthorityTransaction(paths);
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(
            paths,
            transaction,
            new ToolPermissionService(paths, DirectoryPermissionPolicy.Create(paths, permissions)),
            new FixedTimeProvider(WorkspaceToolAuthorityTestFixture.Now.AddMinutes(1)));
        var facade = GovernedLoopEffectAttemptFactory.Create(
            paths,
            trust,
            transaction,
            registry,
            new DirectAuthorityBoundary(transaction),
            new FixedTimeProvider(WorkspaceToolAuthorityTestFixture.Now.AddMinutes(1)));
        var executor = new GovernedLoopWorkspaceActionExecutor(facade);
        var node = fixture.Plan.Nodes.Single(candidate => string.Equals(candidate.NodeId, WorkspaceToolAuthorityTestFixture.NodeId, StringComparison.Ordinal));
        var activation = GovernedLoopNodeExecutionEvidence.CreateActivation(
            node.Ordinal,
            node.Ordinal,
            1,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeIds,
            node.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId);
        var request = new GovernedLoopWorkspaceActionExecutionRequest(
            new GovernedLoopSequentialNodeDispatchRequest(
                GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
                fixture.Anchor,
                fixture.Plan,
                node,
                activation,
                WorkspaceToolAuthorityTestFixture.NodeAttempt),
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ActionInputJson);

        var committed = await executor.ExecuteAsync(request);
        var replayed = await executor.ExecuteAsync(request);

        Assert.Equal("replacement", File.ReadAllText(workspace.File("shared", "note.txt")));
        Assert.Equal(GovernedLoopWorkspaceActionExecutionStatus.Completed, committed.Status);
        Assert.True(WorkspaceActionResultContract.TryParse(committed.CanonicalOutput, out var committedResult));
        Assert.Equal(WorkspaceActionResultStatus.Committed, committedResult!.Status);
        Assert.Equal(GovernedLoopWorkspaceActionExecutionStatus.Completed, replayed.Status);
        Assert.True(WorkspaceActionResultContract.TryParse(replayed.CanonicalOutput, out var replayedResult));
        Assert.Equal(WorkspaceActionResultStatus.Replayed, replayedResult!.Status);
        Assert.Equal(committedResult.AfterEvidenceId, replayedResult.AfterEvidenceId);
    }

    [Fact]
    public async Task Tool_projection_emits_one_stable_local_reversible_effect_request_with_canonical_input()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create(actionNode: true);
        var service = new CapturingEffectService();
        var facade = new GovernedLoopEffectAttemptFacade(new UnusedCatalog(), service);
        var pin = Assert.Single(fixture.Receipt.Evidence.CapabilityAdmission.Pins, candidate =>
            candidate.DescriptorIdentity.Id.Value == WorkspaceToolAuthorityTestFixture.WorkspaceCommandCapabilityId);
        var input = Input("shared/note.txt", "replacement");
        var request = fixture.ToolRequest with
        {
            Command = ToolCommand.Write,
            Content = WorkspaceActionInputContract.Encode(input),
        };
        var executor = new GovernedWorkspaceMutationToolExecutor(
            facade,
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            pin);

        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(request));
        var captured = Assert.IsType<GovernedLoopEffectAttemptRequest>(service.Request);

        Assert.Equal(WorkspaceActionOperationIds.Write, captured.ActuatorOperationId);
        Assert.Equal(WorkspaceActionInputContract.Encode(input), captured.InputJson);
        Assert.Equal(pin, captured.CapabilityPin);
        Assert.Equal(CapabilitySideEffectClass.LocalReversible, captured.RequiredAuthority.MaxSideEffectClass);
        Assert.Equal(1, captured.RequiredAuthority.MaxTargetCount);
        Assert.Equal(pin.DescriptorIdentity, Assert.Single(captured.RequiredAuthority.Capabilities));
        Assert.StartsWith("effect-", captured.EffectId, StringComparison.Ordinal);
        Assert.StartsWith("operation-", captured.IdempotencyOperationId, StringComparison.Ordinal);
        Assert.Equal(1, captured.EffectGeneration);
    }

    [Fact]
    public async Task Tool_projection_rejects_legacy_raw_content_and_target_substitution_before_effect_service()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create(actionNode: true);
        var service = new CapturingEffectService();
        var pin = Assert.Single(fixture.Receipt.Evidence.CapabilityAdmission.Pins, candidate =>
            candidate.DescriptorIdentity.Id.Value == WorkspaceToolAuthorityTestFixture.WorkspaceCommandCapabilityId);
        var executor = new GovernedWorkspaceMutationToolExecutor(
            new GovernedLoopEffectAttemptFacade(new UnusedCatalog(), service),
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            pin);

        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(fixture.ToolRequest with
        {
            Command = ToolCommand.Write,
            Content = "legacy raw content",
        }));
        var mismatched = Input("shared/other.txt", "replacement");
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(fixture.ToolRequest with
        {
            Command = ToolCommand.Write,
            Content = WorkspaceActionInputContract.Encode(mismatched),
        }));
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(fixture.ToolRequest with
        {
            Command = ToolCommand.Write,
            TargetPath = "shared\\note.txt",
            Content = WorkspaceActionInputContract.Encode(Input("shared/note.txt", "replacement")),
        }));

        Assert.Null(service.Request);
    }

    [Fact]
    public void Tool_projection_rejects_an_inference_node_instead_of_building_an_unexecutable_effect_request()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var pin = Assert.Single(fixture.Receipt.Evidence.CapabilityAdmission.Pins, candidate =>
            candidate.DescriptorIdentity.Id.Value == WorkspaceToolAuthorityTestFixture.WorkspaceCommandCapabilityId);

        var exception = Assert.Throws<ArgumentException>(() => new GovernedWorkspaceMutationToolExecutor(
            new GovernedLoopEffectAttemptFacade(new UnusedCatalog(), new CapturingEffectService()),
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            pin));

        Assert.Equal("nodeId", exception.ParamName);
    }

    private static WorkspaceActionInput Input(string targetValue, string value)
    {
        Assert.True(WorkspaceActionScopeId.TryParse("workspace", out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse(targetValue, out var target, out _));
        return new WorkspaceActionInput(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            WorkspaceActionKind.Write,
            scope!,
            target!,
            new WorkspaceActionPrecondition(
                WorkspaceActionPreconditionKind.ExpectedContentHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("before"))).ToLowerInvariant(),
                null,
                null,
                null),
            [new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.LiteralUtf8, value, null)]);
    }

    private sealed class CapturingEffectService : IGovernedLoopEffectAttemptService
    {
        public GovernedLoopEffectAttemptRequest? Request { get; private set; }

        public Task<GovernedLoopEffectAttemptExecutionResult> ExecuteAsync(
            GovernedLoopEffectAttemptRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new GovernedLoopEffectAttemptExecutionResult(
                GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted,
                null,
                "test stop"));
        }
    }

    private sealed class DirectAuthorityBoundary(ICapabilityAuthorityTransaction authorityTransaction) : IGovernedLoopEffectAuthorityDecisionBoundary
    {
        public ICapabilityAuthorityTransaction AuthorityTransaction { get; } = authorityTransaction;

        public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
            => ExecuteWithDecisionAsync(request, (_, token) => commit(token), cancellationToken);

        public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = request.AdmissionReceipt;
            var proof = new GovernedLoopEffectAuthorityProof(
                GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
                receipt.Intent.AuthorityGrant,
                new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
                AuthorityGrantLifecycleStatus.Active,
                GovernedLoopEffectAuthorityGrantPosture.Active,
                receipt.Evidence.GrantBoundary,
                receipt.Evidence.EffectiveAuthority,
                receipt.Evidence.CapabilityAdmission.Pins,
                [],
                receipt.Evidence.GrantDependencyEvidenceHash);
            var decision = GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
                GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
                request.ExecutionBinding.RunId,
                request.ExecutionBinding.ExecutionGeneration,
                request.NodeId,
                request.NodeAttempt,
                request.EffectOperationId,
                request.CorrelationId,
                request.BoundaryKind,
                receipt.ContentHash,
                proof,
                proof,
                request.RequiredAuthority,
                request.RequiredAuthority,
                request.RequiredCapabilityPins,
                GovernedLoopEffectAuthorityDisposition.Direct,
                GovernedLoopEffectAuthorityReason.ActiveExact,
                WorkspaceToolAuthorityTestFixture.Now.AddMinutes(1),
                string.Empty));
            var result = await commit(decision, cancellationToken);
            return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                GovernedLoopEffectAuthorityExecutionStatus.Decided,
                decision,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                true,
                result,
                "direct",
                decision.ContentHash);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class UnusedCatalog : IGovernedActuatorCatalogResolver
    {
        public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The tool projection must not read the catalog directly.");

        public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(
            CapabilityAdmissionPin pin,
            string operationId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The capturing service owns catalog resolution.");
    }
}
