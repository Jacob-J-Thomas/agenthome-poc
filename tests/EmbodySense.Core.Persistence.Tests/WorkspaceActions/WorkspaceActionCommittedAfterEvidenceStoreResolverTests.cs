using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.WorkspaceActions;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.WorkspaceActions;

public sealed class WorkspaceActionCommittedAfterEvidenceStoreResolverTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");

    [Fact]
    public async Task Exact_workspace_outcome_must_match_one_canonical_committed_effect_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, after, outcome) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        await evidence.RetainAfterAsync(after);
        await evidence.RetainOutcomeAsync(outcome);
        var resolver = new WorkspaceActionCommittedAfterEvidenceStoreResolver(paths, evidence);
        var presence = new WorkspaceActionAttemptStorePresenceResolver(paths);

        Assert.False(await resolver.IsCommittedAsync("effect-alpha", "operation-alpha", 1, after.EvidenceId, after.ContentHashOfRecord));
        Assert.Equal(
            WorkspaceActionAttemptPresence.NotFound,
            await presence.ResolveAsync("effect-alpha", "operation-alpha", 1, before.EvidenceId));

        await CommitEffectAsync(paths, before, after, outcome);

        Assert.True(await resolver.IsCommittedAsync("effect-alpha", "operation-alpha", 1, after.EvidenceId, after.ContentHashOfRecord));
        Assert.Equal(
            WorkspaceActionAttemptPresence.ArtifactReleased,
            await presence.ResolveAsync("effect-alpha", "operation-alpha", 1, before.EvidenceId));
        Assert.Equal(
            WorkspaceActionAttemptPresence.Unknown,
            await presence.ResolveAsync("effect-other", "operation-alpha", 1, before.EvidenceId));
        Assert.False(await resolver.IsCommittedAsync("effect-alpha", "operation-alpha", 1, after.EvidenceId, Hash('0')));
        Assert.False(await resolver.IsCommittedAsync("effect-alpha", "operation-other", 1, after.EvidenceId, after.ContentHashOfRecord));
    }

    [Fact]
    public async Task Expired_preparation_cleanup_removes_only_an_exact_unreferenced_before_record()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, _, _) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);

        Assert.Equal(
            new WorkspaceActionPreparationCleanupResult(true, 1),
            await resolver.TryCleanupPreparationsAsync([before], 1));

        Assert.Null(await evidence.ReadBeforeAsync(before.EvidenceId));
    }

    [Fact]
    public async Task Expired_preparation_cleanup_preserves_before_evidence_referenced_by_any_attempt_version()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, after, outcome) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        await CommitEffectAsync(paths, before, after, outcome);
        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);

        Assert.Equal(
            new WorkspaceActionPreparationCleanupResult(true, 0),
            await resolver.TryCleanupPreparationsAsync([before], 1));

        Assert.Equal(before, await evidence.ReadBeforeAsync(before.EvidenceId));
    }

    [Fact]
    public async Task Expired_preparation_cleanup_fails_closed_when_before_is_missing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, _, _) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        File.Delete(Path.Combine(
            paths.AgentPath,
            "loops",
            "execution",
            "workspace-actions",
            "before",
            before.EvidenceId + ".json"));

        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);
        Assert.Equal(
            new WorkspaceActionPreparationCleanupResult(false, 0),
            await resolver.TryCleanupPreparationsAsync([before], 1));
    }

    [Fact]
    public async Task Referenced_oldest_preparation_does_not_starve_later_unreferenced_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (referenced, after, outcome) = Evidence();
        var abandoned = Recapture(referenced, _now.AddSeconds(1));
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(referenced);
        await evidence.RetainBeforeAsync(abandoned);
        await CommitEffectAsync(paths, referenced, after, outcome);
        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);

        Assert.Equal(
            new WorkspaceActionPreparationCleanupResult(true, 1),
            await resolver.TryCleanupPreparationsAsync([referenced, abandoned], 1));

        Assert.Equal(referenced, await evidence.ReadBeforeAsync(referenced.EvidenceId));
        Assert.Null(await evidence.ReadBeforeAsync(abandoned.EvidenceId));
    }

    [Fact]
    public async Task Expired_preparation_cleanup_fails_closed_when_attempt_evidence_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, _, _) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, "unsupported.tmp"), "tampered");
        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);

        Assert.Equal(
            WorkspaceActionPreparationCleanupResult.Unknown,
            await resolver.TryCleanupPreparationsAsync([before], 1));

        Assert.Equal(before, await evidence.ReadBeforeAsync(before.EvidenceId));
    }

    [Fact]
    public async Task Preparation_claim_and_intent_publication_exclude_unreferenced_evidence_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (before, after, _) = Evidence();
        var evidence = new WorkspaceActionEvidenceStore(paths);
        await evidence.RetainBeforeAsync(before);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = PrepareEffect(before, after);
        var claimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginTask = store.BeginWithPreparationClaimAsync(
            prepared,
            async _ =>
            {
                claimEntered.SetResult();
                await releaseClaim.Task;
                return await evidence.ReadBeforeAsync(before.EvidenceId) is not null;
            });
        await claimEntered.Task;
        var cleanupTask = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence)
            .TryCleanupPreparationsAsync([before], 1);

        releaseClaim.SetResult();
        var begun = await beginTask;
        var cleanup = await cleanupTask;

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, begun.Status);
        begun.Lease!.Dispose();
        Assert.Equal(new WorkspaceActionPreparationCleanupResult(true, 0), cleanup);
        Assert.Equal(before, await evidence.ReadBeforeAsync(before.EvidenceId));
    }

    private static async Task CommitEffectAsync(
        WorkspacePaths paths,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionAfterEvidence after,
        WorkspaceActionOutcomeEvidence outcome)
    {
        var prepared = PrepareEffect(before, after);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(
            authorized,
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            _now.AddSeconds(2));
        var observed = GovernedLoopEffectAttemptContract.Advance(
            crossed,
            GovernedLoopEffectPhase.OutcomeObserved,
            GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectEvidenceStatus.Complete,
            outcome.EvidenceId,
            after.EvidenceId,
            _now.AddSeconds(3));
        var committed = GovernedLoopEffectAttemptContract.Advance(
            observed,
            GovernedLoopEffectPhase.Committed,
            GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectEvidenceStatus.Complete,
            outcome.EvidenceId,
            after.EvidenceId,
            _now.AddSeconds(4));
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, begun.Status);
        using var lease = begun.Lease!;
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.ContentHash, authorized, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.ContentHash, crossed, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(crossed.ContentHash, observed, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(observed.ContentHash, committed, lease)).Status);
    }

    private static GovernedLoopEffectAttempt PrepareEffect(WorkspaceActionBeforeEvidence before, WorkspaceActionAfterEvidence after)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/workspace-command", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('1'), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('a'));
        var binding = GovernedLoopExecutionBinding.Create(1, "run-1", revision, 1);
        return GovernedLoopEffectAttemptContract.Prepare(
            binding,
            "action-1",
            1,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, "workspace-command"),
            WorkspaceActionOperationIds.Write,
            Hash('b'),
            after.EffectId,
            after.IdempotencyOperationId,
            after.EffectGeneration,
            Hash('2'),
            after.TargetFingerprint,
            before.PreconditionEvidenceHash,
            Hash('5'),
            before.EvidenceId,
            _now);
    }

    private static (WorkspaceActionBeforeEvidence Before, WorkspaceActionAfterEvidence After, WorkspaceActionOutcomeEvidence Outcome) Evidence()
    {
        Assert.True(WorkspaceActionScopeId.TryParse("workspace", out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse("notes/file.txt", out var target, out _));
        var before = WorkspaceActionEvidenceContract.CreateBefore(
            scope!, target!, Hash('3'), Hash('4'), WorkspaceActionEntryKind.RegularFile,
            FileSystemOperation.Modify, Hash('5'), Hash('6'), Hash('7'), Hash('8'), Hash('9'), 6, 0, _now);
        var after = WorkspaceActionEvidenceContract.CreateAfter(
            before, WorkspaceActionOperationIds.Write, "effect-alpha", "operation-alpha", 1,
            WorkspaceActionEntryKind.RegularFile, Hash('7'), Hash('9'), 5, 0, 1, null, null, _now.AddSeconds(3));
        return (before, after, WorkspaceActionEvidenceContract.CreateOutcome(after));
    }

    private static WorkspaceActionBeforeEvidence Recapture(WorkspaceActionBeforeEvidence before, DateTimeOffset capturedAtUtc)
    {
        Assert.True(WorkspaceActionScopeId.TryParse(before.ScopeId, out var scope));
        Assert.True(WorkspaceRelativeFileTarget.TryParse(before.TargetReference, out var target, out _));
        return WorkspaceActionEvidenceContract.CreateBefore(
            scope!,
            target!,
            before.TargetFingerprint,
            before.PreconditionEvidenceHash,
            before.EntryKind,
            before.PermissionOperation,
            before.PermissionPolicyHash,
            before.RootIdentityFingerprint,
            before.ParentIdentityFingerprint,
            before.NativeIdentityFingerprint,
            before.ContentHash,
            before.ByteCount,
            before.GovernedVersion,
            capturedAtUtc);
    }

    private static string Hash(char value) => new(value, 64);
}
