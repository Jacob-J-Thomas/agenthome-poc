using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.WorkspaceActions;
using EmbodySense.Core.Persistence.WorkspaceActions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.WorkspaceActions;

public sealed class WorkspaceActionNativeHostTests
{
    private const string WorkerBeforeVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_BEFORE";
    private const string WorkerFailpointMarkerVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_FAILPOINT_MARKER";
    private const string WorkerExitBeforeMutationVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_EXIT_BEFORE_MUTATION";
    private const string WorkerTargetVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_TARGET";
    private const string WorkerExitAfterWindowsReplacementSystemCallVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_EXIT_AFTER_WINDOWS_REPLACE";
    private const string WorkerInputVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_INPUT";
    private const string WorkerKindVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_KIND";
    private const string WorkerRootVariable = "EMBODYSENSE_WORKSPACE_ACTION_WORKER_ROOT";

    [Fact]
    public async Task Windows_private_workspace_action_root_reopens_nested_children_with_exact_current_user_acl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/private-acl.txt", ExpectedAbsent(), "first");
        var host = Host(paths);

        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        var targetLocks = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "target-locks");
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        AssertCurrentUserPrivateDirectorySecurity(targetLocks);
        AssertCurrentUserPrivateDirectorySecurity(staging);
        var originalNestedChildren = Directory.EnumerateFiles(targetLocks).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains(originalNestedChildren, path => string.Equals(Path.GetFileName(path), ".custom-loop-mutations.lock", StringComparison.Ordinal));
        Assert.Contains(originalNestedChildren, path => Path.GetFileName(path).StartsWith("shard-", StringComparison.Ordinal));
        Assert.All(originalNestedChildren, AssertCurrentUserPrivateFileSecurity);

        var reopened = await Host(paths).PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/private-acl.txt",
            ExpectedHash("first"),
            "second"));

        Assert.NotNull(reopened);
        Assert.Equal(originalNestedChildren, Directory.EnumerateFiles(targetLocks).Order(StringComparer.Ordinal));
        AssertCurrentUserPrivateDirectorySecurity(targetLocks);
        Assert.All(Directory.EnumerateFiles(targetLocks), AssertCurrentUserPrivateFileSecurity);
    }

    [Fact]
    public async Task ExistingWriteRequiresFreshModifyPermissionRatherThanStaleCreateClassification()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        await File.WriteAllTextAsync(workspace.File("notes", "classified.txt"), "before");
        var permission = new MutablePermissionRevalidator(FileSystemOperation.Create);
        var host = Host(new WorkspacePaths(workspace.RootPath), permissionRevalidator: permission);
        var input = Input(WorkspaceActionKind.Write, "notes/classified.txt", ExpectedHash("before"), "after");

        var prepared = await host.PrepareAsync(input);

        Assert.Null(prepared);
        Assert.Equal([FileSystemOperation.Modify], permission.Operations);
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.File("notes", "classified.txt")));
    }

    [Fact]
    public async Task PermissionPolicyChangeBeforeDispatchStopsWithoutCrossingOrMutation()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "policy.txt");
        await File.WriteAllTextAsync(path, "before");
        var permission = new MutablePermissionRevalidator(FileSystemOperation.Modify);
        var host = Host(new WorkspacePaths(workspace.RootPath), permissionRevalidator: permission);
        var input = Input(WorkspaceActionKind.Write, "notes/policy.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        permission.PolicyHash = Sha256("changed-policy");
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.CrossCount);
        Assert.Equal("before", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PermissionPolicyChangeAtNativeBoundaryLeavesTargetUnchangedAndCannotReusePreparedAuthority()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "boundary-policy.txt");
        await File.WriteAllTextAsync(path, "before");
        var permission = new MutablePermissionRevalidator(FileSystemOperation.Modify);
        var observer = new CallbackCommitObserver((_, _) => permission.PolicyHash = Sha256("changed-at-boundary"));
        var host = Host(
            new WorkspacePaths(workspace.RootPath),
            commitObserver: observer,
            permissionRevalidator: permission);
        var input = Input(WorkspaceActionKind.Write, "notes/boundary-policy.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(1, boundary.CrossCount);
        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Equal(
            [FileSystemOperation.Modify, FileSystemOperation.Modify, FileSystemOperation.Modify],
            permission.Operations);
    }

    [Fact]
    public async Task WriteExpectedAbsentPublishesExactBytesAndSurvivesHostRestart()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/exact.txt", ExpectedAbsent(), "first\r\nsecond");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal(1, boundary.CrossCount);
        Assert.Equal(Encoding.UTF8.GetBytes("first\r\nsecond"), await File.ReadAllBytesAsync(workspace.File("notes", "exact.txt")));
        var restarted = Host(paths);
        var evidence = new WorkspaceActionEvidenceStore(paths);
        var after = Assert.IsType<WorkspaceActionAfterEvidence>(await evidence.ReadAfterAsync(result.Outcome!.AfterEvidenceId!));
        var outcome = Assert.IsType<WorkspaceActionOutcomeEvidence>(await evidence.ReadOutcomeAsync(result.Outcome.OutcomeEvidenceId));
        Assert.NotEqual(result.Outcome.AfterEvidenceId, result.Outcome.OutcomeEvidenceId);
        Assert.Equal(after.EvidenceId, outcome.AfterEvidenceId);
        var current = Assert.IsType<WorkspaceActionNativePreparation>(await restarted.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/exact.txt",
            ExpectedHash("first\r\nsecond"),
            "replacement")));
        Assert.Equal(prepared.BeforeEvidence.RootIdentityFingerprint, current.BeforeEvidence.RootIdentityFingerprint);
        Assert.Equal(prepared.BeforeEvidence.ParentIdentityFingerprint, current.BeforeEvidence.ParentIdentityFingerprint);
        Assert.Equal(after.TargetFingerprint, current.BeforeEvidence.TargetFingerprint);
        Assert.Equal(after.NativeIdentityFingerprint, current.BeforeEvidence.NativeIdentityFingerprint);
        Assert.Equal(after.ContentHash, current.BeforeEvidence.ContentHash);
        var probe = await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.ProvedOutcomeObserved, probe.Posture);
        Assert.Equal(result.Outcome!.AfterEvidenceId, probe.AfterEvidenceId);
        Assert.Equal(result.Outcome.OutcomeEvidenceId, probe.OutcomeEvidenceId);
    }

    [Fact]
    public async Task ConclusiveAfterEvidenceCannotBecomeNotStartedWhenTargetLaterChanges()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var path = workspace.File("notes", "later-change.txt");
        var input = Input(WorkspaceActionKind.Write, "notes/later-change.txt", ExpectedAbsent(), "committed");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        File.Delete(path);

        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));

        Assert.Equal(WorkspaceActionReconciliationPosture.ProvedOutcomeObserved, probe.Posture);
        Assert.Equal(result.Outcome!.AfterEvidenceId, probe.AfterEvidenceId);
        Assert.Equal(result.Outcome.OutcomeEvidenceId, probe.OutcomeEvidenceId);
    }

    [Fact]
    public async Task Canonical_but_substituted_target_fingerprint_never_reaches_dispatch()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var input = Input(WorkspaceActionKind.Write, "notes/fingerprint.txt", ExpectedAbsent(), "safe");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();
        var substituted = Request(input, prepared.BeforeEvidence) with
        {
            TargetFingerprint = new string(prepared.BeforeEvidence.TargetFingerprint[0] == 'f' ? 'e' : 'f', 64),
        };

        var result = await host.ExecuteAsync(substituted, boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.CrossCount);
        Assert.False(File.Exists(workspace.File("notes", "fingerprint.txt")));
    }

    [Fact]
    public async Task GovernedVersionRequiresExactCommittedEffectProofBeforePreparation()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var initial = Input(WorkspaceActionKind.Write, "notes/versioned.txt", ExpectedAbsent(), "one");
        var initialHost = Host(paths);
        var initialPreparation = Assert.IsType<WorkspaceActionNativePreparation>(await initialHost.PrepareAsync(initial));
        var initialResult = await initialHost.ExecuteAsync(
            Request(initial, initialPreparation.BeforeEvidence),
            new RecordingDispatchBoundary());
        var prior = Assert.IsType<WorkspaceActionAfterEvidence>(await new WorkspaceActionEvidenceStore(paths).ReadAfterAsync(initialResult.Outcome!.AfterEvidenceId!));
        var precondition = new WorkspaceActionPrecondition(
            WorkspaceActionPreconditionKind.ExpectedGovernedVersion,
            null,
            prior.GovernedVersion,
            prior.EvidenceId,
            prior.ContentHashOfRecord);
        var next = Input(WorkspaceActionKind.Write, "notes/versioned.txt", precondition, "two");

        Assert.Null(await Host(paths).PrepareAsync(next));

        var resolver = new RecordingCommittedAfterEvidenceResolver();
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths, committedAfterEvidence: resolver).PrepareAsync(next));

        Assert.Equal(prior.GovernedVersion, prepared.BeforeEvidence.GovernedVersion);
        Assert.Equal(
            (prior.EffectId, prior.IdempotencyOperationId, prior.EffectGeneration, prior.EvidenceId, prior.ContentHashOfRecord),
            resolver.Request);
    }

    [Fact]
    public async Task AppendUsesExactContentPreconditionWithoutNewlineConversion()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "append.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("before"));
        var input = Input(WorkspaceActionKind.Append, "notes/append.txt", ExpectedHash("before"), "\nnext");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal(Encoding.UTF8.GetBytes("before\nnext"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExternalChangeAfterPreparationFailsBeforeDispatchAndPreservesChange()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "stale.txt");
        await File.WriteAllTextAsync(path, "before");
        var input = Input(WorkspaceActionKind.Write, "notes/stale.txt", ExpectedHash("before"), "governed");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        await File.WriteAllTextAsync(path, "external");
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.CrossCount);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task BeforeEvidenceCannotBeSubstitutedAcrossTargets()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var first = Input(WorkspaceActionKind.Write, "notes/first.txt", ExpectedAbsent(), "first");
        var second = Input(WorkspaceActionKind.Write, "notes/second.txt", ExpectedAbsent(), "second");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(first));
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(second, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.CrossCount);
        Assert.False(File.Exists(workspace.File("notes", "first.txt")));
        Assert.False(File.Exists(workspace.File("notes", "second.txt")));
    }

    [Fact]
    public async Task DeleteMovesExactPayloadToAuthenticatedQuarantineAndRetainsTombstone()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "delete.txt");
        await File.WriteAllTextAsync(path, "retained-value");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Delete, "notes/delete.txt", ExpectedHash("retained-value"));
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.False(File.Exists(path));
        var store = new WorkspaceActionEvidenceStore(paths);
        var after = Assert.IsType<WorkspaceActionAfterEvidence>(await store.ReadAfterAsync(result.Outcome!.AfterEvidenceId!));
        var tombstone = Assert.IsType<WorkspaceActionTombstone>(await store.ReadTombstoneAsync(after.TombstoneReference!));
        Assert.DoesNotContain("retained-value", System.Text.Json.JsonSerializer.Serialize(after), StringComparison.Ordinal);
        Assert.DoesNotContain("retained-value", System.Text.Json.JsonSerializer.Serialize(tombstone), StringComparison.Ordinal);
        Assert.Equal("retained-value", await File.ReadAllTextAsync(Path.Combine(
            paths.AgentPath,
            "loops",
            "execution",
            "workspace-actions",
            "quarantine",
            tombstone.QuarantineReference + ".payload")));
        var restarted = Host(paths);
        Assert.Equal(
            WorkspaceActionReconciliationPosture.ProvedOutcomeObserved,
            (await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence))).Posture);
    }

    [Fact]
    public async Task DeleteQuarantineByteQuotaFailsBeforeDispatchAndPreservesTarget()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "byte-quota.txt");
        await File.WriteAllTextAsync(path, "12345");
        var quota = new WorkspaceActionStorageLimits(8, 2, 2, 4);
        var host = Host(new WorkspacePaths(workspace.RootPath), quota: quota);
        var input = Input(WorkspaceActionKind.Delete, "notes/byte-quota.txt", ExpectedHash("12345"));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(0, boundary.CrossCount);
        Assert.Equal("12345", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeleteQuarantineCountQuotaFailsBeforeSecondDispatchAndPreservesSecondTarget()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var firstPath = workspace.File("notes", "count-one.txt");
        var secondPath = workspace.File("notes", "count-two.txt");
        await File.WriteAllTextAsync(firstPath, "one");
        await File.WriteAllTextAsync(secondPath, "two");
        var quota = new WorkspaceActionStorageLimits(8, 2, 1, 32);
        var host = Host(new WorkspacePaths(workspace.RootPath), quota: quota);
        var first = Input(WorkspaceActionKind.Delete, "notes/count-one.txt", ExpectedHash("one"));
        var firstPrepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(first));
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.OutcomeObserved,
            (await host.ExecuteAsync(
                Request(first, firstPrepared.BeforeEvidence, "effect-one", "operation-one"),
                new RecordingDispatchBoundary())).Status);
        var second = Input(WorkspaceActionKind.Delete, "notes/count-two.txt", ExpectedHash("two"));
        var secondPrepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(second));
        var secondBoundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.ExecuteAsync(
            Request(second, secondPrepared.BeforeEvidence, "effect-two", "operation-two"),
            secondBoundary));

        Assert.Equal(0, secondBoundary.CrossCount);
        Assert.Equal("two", await File.ReadAllTextAsync(secondPath));
    }

    [Fact]
    public async Task CancellationBeforeBoundaryLeavesTargetAbsent()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var input = Input(WorkspaceActionKind.Write, "notes/cancel.txt", ExpectedAbsent(), "never-written");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary(),
            source.Token));

        Assert.False(File.Exists(workspace.File("notes", "cancel.txt")));
    }

    [Fact]
    public async Task MissingParentAndHardLinkedTargetAreRejectedWithoutMutation()
    {
        using var workspace = new TestWorkspace();
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var missingParent = Input(WorkspaceActionKind.Write, "missing/file.txt", ExpectedAbsent(), "value");
        Assert.ThrowsAny<IOException>(() => host.PrepareAsync(missingParent).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(workspace.File("missing")));

        Directory.CreateDirectory(workspace.File("notes"));
        var source = workspace.File("notes", "source.txt");
        var alias = workspace.File("notes", "alias.txt");
        await File.WriteAllTextAsync(source, "value");
        if (!TryCreateHardLink(alias, source))
        {
            return;
        }
        var hardLinked = Input(WorkspaceActionKind.Write, "notes/source.txt", ExpectedHash("value"), "replacement");
        Assert.ThrowsAny<IOException>(() => host.PrepareAsync(hardLinked).GetAwaiter().GetResult());
        Assert.Equal("value", await File.ReadAllTextAsync(source));
        Assert.Equal("value", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task WindowsMultiLinkedTargetProbeIsIndeterminateRatherThanThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var source = workspace.File("notes", "probe-source.txt");
        var alias = workspace.File("notes", "probe-alias.txt");
        await File.WriteAllTextAsync(source, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/probe-source.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));
        if (!TryCreateHardLink(alias, source))
        {
            return;
        }

        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));

        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Equal("before", await File.ReadAllTextAsync(source));
        Assert.Equal("before", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task WindowsExternalSecondHardLinkAfterPreparationIsDispatchNotStartedWithoutRedispatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var source = workspace.File("notes", "external-link-source.txt");
        var alias = workspace.File("notes", "external-link-alias.txt");
        await File.WriteAllTextAsync(source, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/external-link-source.txt", ExpectedHash("before"), "after");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        if (!TryCreateHardLink(alias, source))
        {
            return;
        }

        var dispatch = new RecordingDispatchBoundary();
        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), dispatch);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, dispatch.CrossCount);
        Assert.Equal("before", await File.ReadAllTextAsync(source));
        Assert.Equal("before", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task WindowsExternalSecondHardLinkWithUninspectableStagingDoesNotProveDispatchNotStarted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var source = workspace.File("notes", "overflow-link-source.txt");
        var alias = workspace.File("notes", "overflow-link-alias.txt");
        await File.WriteAllTextAsync(source, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new WorkspaceActionStorageLimits(8, 1, 2, 32);
        var input = Input(WorkspaceActionKind.Write, "notes/overflow-link-source.txt", ExpectedHash("before"), "after");
        var host = Host(paths, quota: quota);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        if (!TryCreateHardLink(alias, source))
        {
            return;
        }

        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Directory.CreateDirectory(staging);
        for (var index = 0; index < 6; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(staging, $"overflow-{index}.bin"), "uninspectable");
        }

        var dispatch = new RecordingDispatchBoundary();
        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(Request(input, prepared.BeforeEvidence), dispatch));

        Assert.Equal(0, dispatch.CrossCount);
        Assert.Equal("before", await File.ReadAllTextAsync(source));
        Assert.Equal("before", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task DirectNativeCallerCannotBypassClosedInputValidation()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        WorkspaceActionScopeId.TryParse("workspace", out var scope);
        WorkspaceRelativeFileTarget.TryParse("notes/direct.txt", out var target, out _);
        var malformed = new WorkspaceActionInput(
            99,
            WorkspaceActionKind.Write,
            scope!,
            target!,
            ExpectedAbsent(),
            [new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.LiteralUtf8, "value", null)]);

        Assert.Null(await Host(new WorkspacePaths(workspace.RootPath)).PrepareAsync(malformed));
        Assert.False(File.Exists(workspace.File("notes", "direct.txt")));
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write)]
    [InlineData(WorkspaceActionKind.Delete)]
    public async Task ExternalProcessLossBetweenPublishAndEvidenceRequiresReconciliationWithoutRedispatch(WorkspaceActionKind kind)
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var path = workspace.File("notes", "crash.txt");
        var failpointMarker = workspace.File("workspace-action-crash-failpoint.marker");
        var existingWindowsWrite = OperatingSystem.IsWindows() && kind == WorkspaceActionKind.Write;
        if (kind == WorkspaceActionKind.Delete || existingWindowsWrite)
        {
            await File.WriteAllTextAsync(path, "retained-before-crash");
        }
        var input = kind == WorkspaceActionKind.Delete
            ? Input(kind, "notes/crash.txt", ExpectedHash("retained-before-crash"))
            : Input(
                kind,
                "notes/crash.txt",
                existingWindowsWrite ? ExpectedHash("retained-before-crash") : ExpectedAbsent(),
                "committed-before-crash");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(
            startInfo,
            typeof(WorkspaceActionNativeHostTests).Assembly.Location,
            $"{typeof(WorkspaceActionNativeHostTests).FullName}.{nameof(WorkspaceActionCrashWorker)}");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WorkerRootVariable] = workspace.RootPath;
        startInfo.Environment[WorkerBeforeVariable] = prepared.BeforeEvidence.EvidenceId;
        startInfo.Environment[WorkerTargetVariable] = prepared.BeforeEvidence.TargetFingerprint;
        startInfo.Environment[WorkerInputVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkspaceActionInputContract.Encode(input)));
        startInfo.Environment[WorkerKindVariable] = kind.ToString();
        startInfo.Environment[WorkerFailpointMarkerVariable] = failpointMarker;
        using var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("The workspace action crash worker did not start.");
        var output = worker.StandardOutput.ReadToEndAsync();
        var error = worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, worker.ExitCode);
        AssertCrashWorkerReachedFailpoint(failpointMarker);
        _ = await error;
        _ = await output;
        if (kind == WorkspaceActionKind.Write)
        {
            Assert.Equal("committed-before-crash", await File.ReadAllTextAsync(path));
            if (existingWindowsWrite)
            {
                var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
                Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
                var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
                Assert.Equal("retained-before-crash", await File.ReadAllTextAsync(displaced));
                Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
            }
        }
        else
        {
            Assert.False(File.Exists(path));
            var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
            Assert.Single(Directory.EnumerateFiles(quarantine, "*.payload"));
        }
        var restarted = Host(paths);
        var probe = await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Null(probe.AfterEvidenceId);
        if (existingWindowsWrite)
        {
            var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
            clock.Advance(TimeSpan.FromHours(25));
            Assert.Equal(
                0,
                await Host(
                    paths,
                    timeProvider: clock,
                    attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.NotFound)).CleanupOrphansAsync(1));
            var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        }
        var replayBoundary = new RecordingDispatchBoundary();
        var replay = await restarted.ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary);
        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, replay.Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task WorkspaceActionCrashWorker()
    {
        var root = Environment.GetEnvironmentVariable(WorkerRootVariable);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }
        var before = Environment.GetEnvironmentVariable(WorkerBeforeVariable)
            ?? throw new InvalidOperationException("The before-evidence id is required.");
        var targetFingerprint = Environment.GetEnvironmentVariable(WorkerTargetVariable)
            ?? throw new InvalidOperationException("The target fingerprint is required.");
        var encoded = Environment.GetEnvironmentVariable(WorkerInputVariable)
            ?? throw new InvalidOperationException("The canonical workspace input is required.");
        var kindValue = Environment.GetEnvironmentVariable(WorkerKindVariable)
            ?? throw new InvalidOperationException("The workspace action kind is required.");
        var failpointMarker = Environment.GetEnvironmentVariable(WorkerFailpointMarkerVariable)
            ?? throw new InvalidOperationException("The failpoint marker path is required.");
        Assert.True(Enum.TryParse<WorkspaceActionKind>(kindValue, ignoreCase: false, out var kind));
        var canonical = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.True(WorkspaceActionInputContract.TryParse(canonical, kind, out var input, out var reason), reason);
        var exitBeforeMutation = string.Equals(
            Environment.GetEnvironmentVariable(WorkerExitBeforeMutationVariable),
            "1",
            StringComparison.Ordinal);
        var exitAfterWindowsReplacementSystemCall = string.Equals(
            Environment.GetEnvironmentVariable(WorkerExitAfterWindowsReplacementSystemCallVariable),
            "1",
            StringComparison.Ordinal);
        var result = await Host(
            new WorkspacePaths(root),
            observer: exitBeforeMutation ? null : new ExitDurabilityObserver(failpointMarker),
            commitObserver: exitBeforeMutation ? new ExitCommitObserver(failpointMarker) : null,
            namespaceRaceObserver: exitAfterWindowsReplacementSystemCall ? new ExitNamespaceRaceObserver(failpointMarker) : null).ExecuteAsync(
            new WorkspaceActionNativeExecutionRequest(input!, targetFingerprint, before, "effect-alpha", "operation-alpha", 1),
            new RecordingDispatchBoundary());
        throw new InvalidOperationException($"The crash boundary returned unexpectedly with {result.Status}.");
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write, WorkspaceActionDurabilityPoint.AfterInstallBeforeEvidence)]
    [InlineData(WorkspaceActionKind.Delete, WorkspaceActionDurabilityPoint.AfterDeleteTombstoneBeforeEvidence)]
    public async Task PublishedAfterEvidenceFailureIsIndeterminateAndNeverReportedNotStarted(
        WorkspaceActionKind kind,
        WorkspaceActionDurabilityPoint expectedPoint)
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "ambiguous.txt");
        if (kind == WorkspaceActionKind.Delete)
        {
            await File.WriteAllTextAsync(path, "before");
        }
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = kind == WorkspaceActionKind.Delete
            ? Input(kind, "notes/ambiguous.txt", ExpectedHash("before"))
            : Input(kind, "notes/ambiguous.txt", ExpectedAbsent(), "after");
        var observer = new ThrowingDurabilityObserver();
        var host = Host(paths, observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary));

        Assert.Equal(1, boundary.CrossCount);
        Assert.Equal(expectedPoint, observer.Point);
        Assert.Equal(kind == WorkspaceActionKind.Write, File.Exists(path));
        var restarted = Host(paths);
        var probe = await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Null(probe.AfterEvidenceId);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await restarted.ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task ExistingUnixWritePreservesExactPermissionMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "mode.txt");
        await File.WriteAllTextAsync(path, "before");
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, expectedMode);
        var input = Input(WorkspaceActionKind.Write, "notes/mode.txt", ExpectedHash("before"), "after");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        Assert.Equal(
            WorkspaceActionNativeCommitStatus.OutcomeObserved,
            (await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary())).Status);

        Assert.Equal(expectedMode, File.GetUnixFileMode(path));
        Assert.Equal("after", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExistingMacWritePreservesExtendedAccessControlList()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "acl.txt");
        await File.WriteAllTextAsync(path, "before");
        Assert.Equal(0, (await RunProcessAsync("/bin/chmod", "+a", "everyone deny execute", path)).ExitCode);
        var expectedAcl = AccessControlLines((await RunProcessAsync("/bin/ls", "-le", path)).Output);
        Assert.NotEmpty(expectedAcl);
        var input = Input(WorkspaceActionKind.Write, "notes/acl.txt", ExpectedHash("before"), "after");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        Assert.Equal(
            WorkspaceActionNativeCommitStatus.OutcomeObserved,
            (await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary())).Status);

        Assert.Equal(expectedAcl, AccessControlLines((await RunProcessAsync("/bin/ls", "-le", path)).Output));
        Assert.Equal("after", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LinuxDefaultAclOnPrivateStagingIsRemovedBeforeAnExistingWriteCrossesDispatch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "default-acl.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Directory.CreateDirectory(staging);
        if (!TrySetLinuxDefaultAcl(staging))
        {
            return;
        }
        Assert.True(HasLinuxExtendedAttribute(staging, "system.posix_acl_default"));
        Assert.False(HasLinuxExtendedAttribute(path, "system.posix_acl_access"));
        var input = Input(WorkspaceActionKind.Write, "notes/default-acl.txt", ExpectedHash("before"), "after");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal(1, boundary.CrossCount);
        Assert.False(HasLinuxExtendedAttribute(staging, "system.posix_acl_default"));
        Assert.False(HasLinuxExtendedAttribute(path, "system.posix_acl_access"));
        Assert.Equal("after", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(WorkspaceActionAttemptPresence.NotFound, 1)]
    [InlineData(WorkspaceActionAttemptPresence.Exists, 0)]
    [InlineData(WorkspaceActionAttemptPresence.Unknown, 0)]
    public async Task OldAuthenticatedStageIsCleanedOnlyWhenCanonicalIntentIsProvedAbsent(
        WorkspaceActionAttemptPresence presence,
        int expectedRemoved)
    {
        if (OperatingSystem.IsWindows())
        {
            // Authoritative Windows execution remains an explicit external validation requirement.
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "orphan.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        var resolver = new FixedAttemptPresenceResolver(presence);
        var input = Input(WorkspaceActionKind.Write, "notes/orphan.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        _ = await RunCrashWorkerAsync(workspace.RootPath, input, prepared.BeforeEvidence, exitBeforeMutation: true);

        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var marker = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staging));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));

        clock.Advance(TimeSpan.FromHours(25));
        var host = Host(paths, timeProvider: clock, attemptPresence: resolver);
        Assert.Equal(expectedRemoved, await host.CleanupOrphansAsync(1));

        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(expectedRemoved == 0, Directory.EnumerateFiles(staging, "*.stage").Any());
        Assert.Equal(expectedRemoved == 0, Directory.EnumerateFiles(staging, "*.stage.marker").Any());
        Assert.Equal("before", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OrphanCleanupProgressSurvivesHostRestartUnderOneFixedClock()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        foreach (var name in new[] { "first", "second" })
        {
            var path = workspace.File("notes", $"{name}-restart-orphan.txt");
            await File.WriteAllTextAsync(path, "before");
            var input = Input(WorkspaceActionKind.Write, $"notes/{name}-restart-orphan.txt", ExpectedHash("before"), "after");
            var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths, timeProvider: clock).PrepareAsync(input));
            _ = await RunCrashWorkerAsync(workspace.RootPath, input, prepared.BeforeEvidence, exitBeforeMutation: true);
        }
        clock.Advance(TimeSpan.FromHours(25));
        var firstResolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Exists);
        var secondResolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Exists);

        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: firstResolver).CleanupOrphansAsync(1));
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: secondResolver).CleanupOrphansAsync(1));

        Assert.Single(firstResolver.ResolvedBeforeEvidenceIds);
        Assert.Single(secondResolver.ResolvedBeforeEvidenceIds);
        Assert.NotEqual(firstResolver.ResolvedBeforeEvidenceIds[0], secondResolver.ResolvedBeforeEvidenceIds[0]);
    }

    [Fact]
    public async Task RecentAuthenticatedStageIsPreservedWithoutConsultingCanonicalIntent()
    {
        if (OperatingSystem.IsWindows())
        {
            // Authoritative Windows execution remains an explicit external validation requirement.
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "recent-orphan.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.NotFound);
        var input = Input(WorkspaceActionKind.Write, "notes/recent-orphan.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        _ = await RunCrashWorkerAsync(workspace.RootPath, input, prepared.BeforeEvidence, exitBeforeMutation: true);

        var host = Host(paths, attemptPresence: resolver);
        Assert.Equal(0, await host.CleanupOrphansAsync(1));
        Assert.Equal(0, resolver.ResolveCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Equal("before", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CorruptAuthenticatedStageMarkerFailsClosedWithoutDeletingPayload()
    {
        if (OperatingSystem.IsWindows())
        {
            // Authoritative Windows execution remains an explicit external validation requirement.
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "corrupt-orphan.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.NotFound);
        var input = Input(WorkspaceActionKind.Write, "notes/corrupt-orphan.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        _ = await RunCrashWorkerAsync(workspace.RootPath, input, prepared.BeforeEvidence, exitBeforeMutation: true);

        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var marker = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        await File.WriteAllTextAsync(marker, "tampered\n");
        clock.Advance(TimeSpan.FromHours(25));

        var host = Host(paths, timeProvider: clock, attemptPresence: resolver);
        await Assert.ThrowsAsync<FormatException>(() => host.CleanupOrphansAsync(1));
        Assert.Equal(0, resolver.ResolveCount);
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.True(File.Exists(marker));
        Assert.Equal("before", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExactAtomicMarkerTemporaryFromProcessLossIsRemovedButUnknownTemporaryRemainsFailClosed()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var host = Host(paths, attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown));
        Assert.Equal(0, await host.CleanupOrphansAsync(1));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var exactTemporary = Path.Combine(
            staging,
            $".stage-{new string('a', 64)}.stage.marker.{new string('b', 32)}.tmp");
        await File.WriteAllTextAsync(exactTemporary, "partial");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exactTemporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(0, await host.CleanupOrphansAsync(1));
        Assert.False(File.Exists(exactTemporary));

        var unknownTemporary = Path.Combine(staging, "unsupported.tmp");
        await File.WriteAllTextAsync(unknownTemporary, "partial");
        await Assert.ThrowsAsync<FormatException>(() => host.CleanupOrphansAsync(1));
        Assert.True(File.Exists(unknownTemporary));
    }

    [Fact]
    public async Task OperationLeaseFilesRemainWithinTheFixedShardBoundAcrossDistinctParents()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new WorkspaceActionStorageLimits(64, 2, 2, 1_024);
        var host = Host(paths, quota: quota);
        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(workspace.File($"parent-{index:D2}"));
            Assert.NotNull(await host.PrepareAsync(Input(
                WorkspaceActionKind.Write,
                $"parent-{index:D2}/value.txt",
                ExpectedAbsent(),
                index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        var locks = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "target-locks");
        var shards = Directory.EnumerateFiles(locks, "shard-*.lock").ToArray();
        Assert.NotEmpty(shards);
        Assert.True(shards.Length <= quota.MaximumStagingEntries);
        Assert.All(shards, shard => Assert.Equal(0, new FileInfo(shard).Length));
    }

    [Fact]
    public async Task RetainedDeletePayloadDoesNotStarveLaterStageOrphanCleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var deletePath = workspace.File("notes", "retained-delete.txt");
        await File.WriteAllTextAsync(deletePath, "delete-before");
        var deleteInput = Input(WorkspaceActionKind.Delete, "notes/retained-delete.txt", ExpectedHash("delete-before"));
        var deleteHost = Host(paths);
        var deleteBefore = Assert.IsType<WorkspaceActionNativePreparation>(await deleteHost.PrepareAsync(deleteInput));
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.OutcomeObserved,
            (await deleteHost.ExecuteAsync(Request(deleteInput, deleteBefore.BeforeEvidence), new RecordingDispatchBoundary())).Status);

        var stagePath = workspace.File("notes", "later-stage.txt");
        await File.WriteAllTextAsync(stagePath, "stage-before");
        var stageInput = Input(WorkspaceActionKind.Write, "notes/later-stage.txt", ExpectedHash("stage-before"), "stage-after");
        var stageBefore = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(stageInput));
        _ = await RunCrashWorkerAsync(workspace.RootPath, stageInput, stageBefore.BeforeEvidence, exitBeforeMutation: true);

        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(25));
        var cleanup = Host(paths, timeProvider: clock, attemptPresence: resolver);
        Assert.Equal(1, await cleanup.CleanupOrphansAsync(1));
        Assert.Equal(1, resolver.ResolveCount);
        var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
        Assert.Single(Directory.EnumerateFiles(quarantine, "*.payload"));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
    }

    [Fact]
    public async Task ReleasedDeletePayloadIsReclaimedOnlyAfterAuthenticatedRetentionExpiry()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstPath = workspace.File("notes", "first.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        var clock = new MutableWorkspaceActionTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased);
        var quota = new WorkspaceActionStorageLimits(16, 2, 1, 32);
        var cleanupObserver = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.BeforeCleanupArtifactDelete)
            {
                Assert.NotEmpty(Directory.EnumerateFiles(
                    Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine"),
                    "*.payload"));
            }
        });
        var host = Host(
            paths,
            quota: quota,
            timeProvider: clock,
            attemptPresence: resolver,
            namespaceRaceObserver: cleanupObserver);
        var first = Input(WorkspaceActionKind.Delete, "notes/first.txt", ExpectedHash("first"));
        var firstBefore = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(first));
        var firstResult = await host.ExecuteAsync(
            Request(first, firstBefore.BeforeEvidence, "effect-first", "operation-first"),
            new RecordingDispatchBoundary());
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, firstResult.Status);

        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await host.CleanupOrphansAsync(1));
        var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
        Assert.Single(Directory.EnumerateFiles(quarantine, "*.payload"));

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await host.CleanupOrphansAsync(1));
        Assert.Contains(
            WorkspaceActionNamespaceRacePoint.BeforeCleanupArtifactDelete,
            cleanupObserver.Points);
        Assert.Empty(Directory.EnumerateFiles(quarantine, "*.payload"));
        Assert.Empty(Directory.EnumerateFiles(quarantine, "*.reservation"));

        var secondPath = workspace.File("notes", "second.txt");
        await File.WriteAllTextAsync(secondPath, "second");
        var second = Input(WorkspaceActionKind.Delete, "notes/second.txt", ExpectedHash("second"));
        var secondBefore = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(second));
        var secondResult = await host.ExecuteAsync(
            Request(second, secondBefore.BeforeEvidence, "effect-second", "operation-second"),
            new RecordingDispatchBoundary());
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, secondResult.Status);
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write)]
    [InlineData(WorkspaceActionKind.Delete)]
    public async Task AfterEvidenceBeforeOutcomeIsIndeterminateAndNeverRedispatches(WorkspaceActionKind kind)
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var path = workspace.File("notes", "after-before-outcome.txt");
        await File.WriteAllTextAsync(path, "before");
        var input = Input(kind, "notes/after-before-outcome.txt", ExpectedHash("before"), kind == WorkspaceActionKind.Write ? "after" : null);
        var observer = new ThrowingDurabilityObserver(WorkspaceActionDurabilityPoint.AfterEvidenceBeforeOutcome);
        var host = Host(paths, observer: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(WorkspaceActionDurabilityPoint.AfterEvidenceBeforeOutcome, observer.Point);
        Assert.Equal(1, boundary.CrossCount);
        var evidence = new WorkspaceActionEvidenceStore(paths);
        var after = await evidence.FindAfterAsync("effect-alpha", "operation-alpha", 1);
        Assert.NotNull(after);
        Assert.Null(await evidence.FindOutcomeAsync("effect-alpha", "operation-alpha", 1));
        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Equal(after!.EvidenceId, probe.AfterEvidenceId);
        Assert.Null(probe.OutcomeEvidenceId);

        var replayBoundary = new RecordingDispatchBoundary();
        var replay = await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary);
        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, replay.Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task ProbeRejectsMalformedAndMismatchedRetainedEvidenceWithoutMutation()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/probe-validation.txt", ExpectedAbsent(), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        var malformed = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence) with { TargetFingerprint = "invalid" });
        Assert.Equal(WorkspaceActionReconciliationPosture.Unknown, malformed.Posture);

        var mismatchedTarget = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence) with { TargetFingerprint = new string('0', 64) });
        Assert.Equal(WorkspaceActionReconciliationPosture.Unknown, mismatchedTarget.Posture);

        var missingBefore = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence) with { BeforeEvidenceId = "before-" + new string('0', 64) });
        Assert.Equal(WorkspaceActionReconciliationPosture.Unknown, missingBefore.Posture);
        Assert.False(File.Exists(workspace.File("notes", "probe-validation.txt")));
    }

    [Fact]
    public async Task ProbeFailsClosedWhenAfterEvidenceIsMissingButOutcomeRemains()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/probe-after-missing.txt", ExpectedAbsent(), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));
        var result = await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);

        var afterRoot = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "after");
        File.Delete(Path.Combine(afterRoot, result.Outcome!.AfterEvidenceId + ".json"));

        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Unknown, probe.Posture);
        Assert.Null(probe.AfterEvidenceId);
        Assert.Null(probe.OutcomeEvidenceId);
    }

    [Fact]
    public async Task UnsupportedStagingArtifactFailsClosedBeforeNativeDispatch()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/unsupported-stage.txt", ExpectedAbsent(), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "unsupported-artifact.bin"), "unexpected");
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<FormatException>(() => Host(paths).ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(0, boundary.CrossCount);
        Assert.True(File.Exists(Path.Combine(staging, "unsupported-artifact.bin")));
        Assert.False(File.Exists(workspace.File("notes", "unsupported-stage.txt")));
    }

    [Fact]
    public async Task DeleteObserverRepopulationFailsClosedAfterExactNamespaceRename()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var path = workspace.File("notes", "delete-repopulation.txt");
        await File.WriteAllTextAsync(path, "before");
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.AfterDeleteSystemCall)
            {
                File.WriteAllText(path, "repopulated");
            }
        });
        var input = Input(WorkspaceActionKind.Delete, "notes/delete-repopulation.txt", ExpectedHash("before"));
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(1, boundary.CrossCount);
        Assert.Equal("repopulated", await File.ReadAllTextAsync(path));
        var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
        var payload = Assert.Single(Directory.EnumerateFiles(quarantine, "*.payload"));
        Assert.Equal("before", await File.ReadAllTextAsync(payload));
        Assert.Single(Directory.EnumerateFiles(quarantine, "*.reservation"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        Assert.Contains(WorkspaceActionNamespaceRacePoint.BeforeDeleteSystemCall, observer.Points);
        Assert.Contains(WorkspaceActionNamespaceRacePoint.AfterDeleteSystemCall, observer.Points);
    }

    [Fact]
    public async Task Old_unreferenced_preparation_is_boundedly_removed_before_it_can_exhaust_evidence_capacity()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new WorkspaceActionStorageLimits(1, 2, 2, 32);
        var evidence = new WorkspaceActionEvidenceStore(paths, quota);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        var resolver = new WorkspaceActionAttemptStorePresenceResolver(paths, evidence);
        var host = Host(paths, quota: quota, timeProvider: clock, attemptPresence: resolver, evidenceStore: evidence);
        var first = Input(WorkspaceActionKind.Write, "notes/expired.txt", ExpectedAbsent(), "first");
        var expired = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(first));
        clock.Advance(TimeSpan.FromHours(25));
        var second = Input(WorkspaceActionKind.Write, "notes/current.txt", ExpectedAbsent(), "second");

        var prepared = await host.PrepareAsync(second);

        Assert.NotNull(prepared);
        Assert.Null(await evidence.ReadBeforeAsync(expired.BeforeEvidence.EvidenceId));
        Assert.Equal(prepared!.BeforeEvidence, await evidence.ReadBeforeAsync(prepared.BeforeEvidence.EvidenceId));
    }

    [Fact]
    public async Task PreparationCleanupProgressSurvivesHostRestartUnderOneFixedClock()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        foreach (var name in new[] { "first", "second" })
        {
            Assert.NotNull(await Host(paths, timeProvider: clock).PrepareAsync(Input(
                WorkspaceActionKind.Write,
                $"notes/{name}-restart-preparation.txt",
                ExpectedAbsent(),
                name)));
        }
        clock.Advance(TimeSpan.FromHours(25));
        var complete = new WorkspaceActionPreparationCleanupResult(true, 0);
        var firstResolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown, complete);
        var secondResolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown, complete);

        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: firstResolver).CleanupPreparationsAsync(1));
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: secondResolver).CleanupPreparationsAsync(1));

        var firstId = Assert.Single(Assert.Single(firstResolver.PreparationBatches));
        var secondId = Assert.Single(Assert.Single(secondResolver.PreparationBatches));
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task InterruptedPreparationCleanupReplaysItsLeasedWindowAfterHostRestart()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        foreach (var name in new[] { "first", "second" })
        {
            Assert.NotNull(await Host(paths, timeProvider: clock).PrepareAsync(Input(
                WorkspaceActionKind.Write,
                $"notes/{name}-interrupted-cleanup.txt",
                ExpectedAbsent(),
                name)));
        }
        clock.Advance(TimeSpan.FromHours(25));
        var interrupted = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown)
        {
            ThrowOnPreparationCleanup = true,
        };
        var replayed = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown);

        await Assert.ThrowsAsync<IOException>(() => Host(
            paths,
            timeProvider: clock,
            attemptPresence: interrupted).CleanupPreparationsAsync(1));
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: replayed).CleanupPreparationsAsync(1));

        var interruptedId = Assert.Single(Assert.Single(interrupted.PreparationBatches));
        var replayedId = Assert.Single(Assert.Single(replayed.PreparationBatches));
        Assert.Equal(interruptedId, replayedId);
    }

    [Fact]
    public async Task CleanupCursorHardLinkIsRejectedWithoutMutatingTheLinkedFile()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        Assert.NotNull(await Host(paths, timeProvider: clock).PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/hard-link-cursor.txt",
            ExpectedAbsent(),
            "value")));
        clock.Advance(TimeSpan.FromHours(25));
        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown);
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: resolver).CleanupPreparationsAsync(1));
        var progress = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "cleanup-progress");
        var cursor = Path.Combine(progress, "preparations.cursor.0");
        File.Delete(cursor);
        var linked = workspace.File("linked-cursor-target.bin");
        var original = Enumerable.Repeat((byte)0x5a, 4096).ToArray();
        await File.WriteAllBytesAsync(linked, original);
        if (!TryCreateHardLink(cursor, linked))
        {
            return;
        }

        await Assert.ThrowsAsync<IOException>(() => Host(
            paths,
            timeProvider: clock,
            attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown)).CleanupPreparationsAsync(1));

        Assert.Equal(original, await File.ReadAllBytesAsync(linked));
    }

    [Fact]
    public async Task PrivateArtifactAncestorSubstitutionFailsBeforeDispatchWithoutMutatingOutsideTheWorkspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var target = workspace.File("notes", "private-ancestor.txt");
        await File.WriteAllTextAsync(target, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/private-ancestor.txt", ExpectedHash("before"), "after");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var privateParent = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var outside = workspace.File("..", $"outside-private-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "artifact.bin");
        await File.WriteAllTextAsync(outsideFile, "outside");
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outsideFile, expectedMode);
        Directory.CreateSymbolicLink(privateParent, outside);
        try
        {
            var boundary = new RecordingDispatchBoundary();

            await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
                Request(input, prepared.BeforeEvidence),
                boundary));

            Assert.Equal(0, boundary.CrossCount);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
            Assert.Equal(expectedMode, File.GetUnixFileMode(outsideFile));
            Assert.Equal("before", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(privateParent);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task PartialCursorInitializerIsRecoveredAndOneTornSlotRetainsTheOtherFailureDomain()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        Assert.NotNull(await Host(paths, timeProvider: clock).PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/cursor-recovery.txt",
            ExpectedAbsent(),
            "value")));
        clock.Advance(TimeSpan.FromHours(25));
        var resolver = new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.Unknown);
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: resolver).CleanupPreparationsAsync(1));
        var progress = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "cleanup-progress");
        var firstCursor = Path.Combine(progress, "preparations.cursor.0");
        var secondCursor = Path.Combine(progress, "preparations.cursor.1");
        var initializer = Path.Combine(progress, ".preparations.cursor.0.initializing");
        File.Delete(firstCursor);
        await File.WriteAllBytesAsync(initializer, [1, 2, 3]);

        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: resolver).CleanupPreparationsAsync(1));
        Assert.False(File.Exists(initializer));
        Assert.Equal(4096, new FileInfo(firstCursor).Length);

        await File.WriteAllBytesAsync(firstCursor, Enumerable.Repeat((byte)0xff, 4096).ToArray());
        Assert.Equal(0, await Host(paths, timeProvider: clock, attemptPresence: resolver).CleanupPreparationsAsync(1));

        await File.WriteAllBytesAsync(secondCursor, Enumerable.Repeat((byte)0xff, 4096).ToArray());
        await Assert.ThrowsAsync<FormatException>(() => Host(
            paths,
            timeProvider: clock,
            attemptPresence: resolver).CleanupPreparationsAsync(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task PreparationCleanupRejectsUnboundedPassSize(int maximumPreparations)
    {
        using var workspace = new TestWorkspace();
        var host = Host(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.CleanupPreparationsAsync(maximumPreparations));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task OrphanCleanupRejectsUnboundedPassSize(int maximumArtifacts)
    {
        using var workspace = new TestWorkspace();
        var host = Host(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.CleanupOrphansAsync(maximumArtifacts));
    }

    [Fact]
    public async Task DeleteFailureBeforeNamespaceMutationRemovesExactAuthenticatedReservationAndPlaceholder()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "delete-orphan.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var observer = new CallbackCommitObserver((_, _) => throw new IOException("Injected failure before target mutation."));
        var host = Host(paths, commitObserver: observer);
        var input = Input(WorkspaceActionKind.Delete, "notes/delete-orphan.txt", ExpectedHash("before"));
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(quarantine, "*.reservation"));
        Assert.Empty(Directory.EnumerateFiles(quarantine, "*.payload"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(quarantine));
        }
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Append, "beforeafter")]
    [InlineData(WorkspaceActionKind.Write, "after")]
    public async Task ExistingWindowsInstallAtomicallyPublishesAndAuthenticatesDisplacedTarget(
        WorkspaceActionKind kind,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "existing.txt");
        await File.WriteAllTextAsync(path, "before");
        var input = Input(kind, "notes/existing.txt", ExpectedHash("before"), "after");
        var paths = new WorkspacePaths(workspace.RootPath);
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var boundary = new RecordingDispatchBoundary();
        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.NotNull(result.Outcome);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(1, boundary.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.marker"));
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Append, "beforeafter")]
    [InlineData(WorkspaceActionKind.Write, "after")]
    public async Task ExistingWindowsReplacementPreservesControlledOwnerGroupAndDaclExactly(
        WorkspaceActionKind kind,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "replacement-security.txt");
        await File.WriteAllTextAsync(path, "before");
        ConfigureControlledWindowsReplacementSecurity(path);
        var expectedSecurity = CaptureWindowsSecurityDescriptor(path);

        var input = Input(kind, "notes/replacement-security.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(new WorkspacePaths(workspace.RootPath)).PrepareAsync(input));
        var result = await Host(new WorkspacePaths(workspace.RootPath)).ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary());

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        AssertSameWindowsReplacementSecurity(expectedSecurity, CaptureWindowsSecurityDescriptor(path));
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Append, "beforeafter")]
    [InlineData(WorkspaceActionKind.Write, "after")]
    public async Task ExistingWindowsReplacementPreservesInheritedUnprotectedDaclExactly(
        WorkspaceActionKind kind,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        ConfigureInheritedWindowsReplacementParentSecurity(workspace.File("notes"));
        var path = workspace.File("notes", "replacement-inherited-security.txt");
        await File.WriteAllTextAsync(path, "before");
        var expectedSecurity = CaptureWindowsSecurityDescriptor(path);
        AssertInheritedUnprotectedWindowsAccessControl(expectedSecurity);

        var input = Input(kind, "notes/replacement-inherited-security.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(new WorkspacePaths(workspace.RootPath)).PrepareAsync(input));
        var result = await Host(new WorkspacePaths(workspace.RootPath)).ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary());

        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        AssertSameWindowsReplacementSecurity(expectedSecurity, CaptureWindowsSecurityDescriptor(path));
    }

    [Fact]
    public async Task ExistingWindowsReplacementRejectsCompatibleExternalWriteBeforePublication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-race.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall)
            {
                return;
            }
            WriteAllTextWithCompatibleSharing(path, "external");
        });
        var input = Input(WorkspaceActionKind.Write, "notes/windows-race.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("external", await File.ReadAllTextAsync(path));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task ExistingWindowsReplacementFailsClosedWhenAncestorRenameAndDeletionRaceTheFinalCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var notes = workspace.File("notes");
        var path = workspace.File("notes", "windows-fence.txt");
        var movedNotes = workspace.File("notes-moved");
        await File.WriteAllTextAsync(path, "before");
        var renameBlocked = false;
        var deleteBlocked = false;
        var targetDeleted = false;
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall)
            {
                return;
            }
            try
            {
                Directory.Move(notes, movedNotes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }
            try
            {
                File.Delete(path);
                targetDeleted = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                targetDeleted = false;
            }
            try
            {
                Directory.Delete(notes, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deleteBlocked = true;
            }
        });
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-fence.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.True(renameBlocked);
        Assert.True(deleteBlocked);
        Assert.True(targetDeleted);
        Assert.False(Directory.Exists(movedNotes));
        Assert.True(Directory.Exists(notes));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(notes),
            entry => string.Equals(Path.GetFileName(entry), Path.GetFileName(path), StringComparison.Ordinal));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task ExistingWindowsReplacementFencesDisplacedBackupAfterPublication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-displaced-fence.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var writeBlocked = false;
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall)
            {
                return;
            }
            var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            try
            {
                WriteAllTextWithCompatibleSharing(displaced, "external");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeBlocked = true;
            }
        });
        var input = Input(WorkspaceActionKind.Write, "notes/windows-displaced-fence.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());

        Assert.True(writeBlocked);
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal("governed", await File.ReadAllTextAsync(path));
        Assert.NotNull(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
    }

    [Fact]
    public async Task ExistingWindowsReplacementFenceBlocksExternalWriteBeforeFinalPublication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-write-race.txt");
        await File.WriteAllTextAsync(path, "before");
        var writeBlocked = false;
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.AfterWindowsReplacementFinalCheckBeforeReplaceSystemCall)
            {
                try
                {
                    WriteAllTextWithCompatibleSharing(path, "external");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    writeBlocked = true;
                }
            }
        });
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-write-race.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), new RecordingDispatchBoundary());

        Assert.True(writeBlocked);
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.Equal("governed", await File.ReadAllTextAsync(path));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.NotNull(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingWindowsReplacementMissingOrSubstitutedBackupAfterNativeBoundaryRequiresReconciliation(bool substitute)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-backup-tamper.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var publishedTargetOpenBlocked = false;
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != WorkspaceActionNamespaceRacePoint.AfterWindowsReplacementSystemCallBeforeBackupRetention)
            {
                return;
            }
            try
            {
                WriteAllTextWithCompatibleSharing(path, "external");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                publishedTargetOpenBlocked = true;
            }
            var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            File.Delete(displaced);
            if (substitute)
            {
                File.WriteAllText(displaced, "external");
            }
        });
        var input = Input(WorkspaceActionKind.Write, "notes/windows-backup-tamper.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("governed", await File.ReadAllTextAsync(path));
        Assert.True(publishedTargetOpenBlocked);
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        var original = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        if (substitute)
        {
            var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Equal("external", await File.ReadAllTextAsync(displaced));
        }
        else
        {
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        }
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        Assert.Equal(
            WorkspaceActionReconciliationPosture.Indeterminate,
            (await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence))).Posture);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);
        if (!substitute)
        {
            var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
            clock.Advance(TimeSpan.FromHours(25));

            Assert.Equal(
                0,
                await Host(
                    paths,
                    timeProvider: clock,
                    attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.NotFound)).CleanupOrphansAsync(1));

            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
            Assert.Equal(
                0,
                await Host(
                    paths,
                    timeProvider: clock,
                    attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased)).CleanupOrphansAsync(1));

            Assert.Equal("governed", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        }
    }

    [Fact]
    public async Task WindowsOriginalWitnessBeforeReplacementIsCleanedOnlyAfterExactTargetProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-original-cleanup.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        var input = Input(WorkspaceActionKind.Write, "notes/windows-original-cleanup.txt", ExpectedHash("before"), "governed");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths, timeProvider: clock).PrepareAsync(input));
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.AfterWindowsReplacementFinalCheckBeforeReplaceSystemCall)
            {
                throw new IOException("Stop after retaining the exact original witness.");
            }
        });

        await Assert.ThrowsAsync<IOException>(() => Host(paths, timeProvider: clock, namespaceRaceObserver: observer).ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var original = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));

        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(
            1,
            await Host(
                paths,
                timeProvider: clock,
                attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.NotFound)).CleanupOrphansAsync(1));

        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
    }

    [Fact]
    public async Task WindowsAtomicReplacementCrashBeforeBackupRetentionIsIndeterminateAndReleasedCleanupIsFenced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-replace-crash.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-replace-crash.txt", ExpectedHash("before"), "governed");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        _ = await RunCrashWorkerAsync(
            workspace.RootPath,
            input,
            prepared.BeforeEvidence,
            exitBeforeMutation: false,
            exitAfterWindowsReplacementSystemCall: true);

        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Equal("governed", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        var original = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Equal("before", await File.ReadAllTextAsync(displaced));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        var restarted = Host(paths);
        Assert.Equal(
            WorkspaceActionReconciliationPosture.Indeterminate,
            (await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence))).Posture);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await restarted.ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);

        var writeBlocked = false;
        var deleteBlocked = false;
        var cleanupObserver = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != WorkspaceActionNamespaceRacePoint.BeforeCleanupArtifactDelete)
            {
                return;
            }
            var retained = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            try
            {
                WriteAllTextWithCompatibleSharing(retained, "external");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeBlocked = true;
            }
            try
            {
                File.Delete(retained);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deleteBlocked = true;
            }
        });
        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(
            1,
            await Host(
                paths,
                timeProvider: clock,
                attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased),
                namespaceRaceObserver: cleanupObserver).CleanupOrphansAsync(1));

        Assert.True(writeBlocked);
        Assert.True(deleteBlocked);
        Assert.Equal("governed", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
    }

    [Theory]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToRemoveReplaced)]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToMoveReplacement)]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToMoveReplacement2)]
    public async Task WindowsPartialReplaceFileFailuresRetainEvidenceAndNeverRedispatch(int nativeErrorCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-partial-replace.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-partial-replace.txt", ExpectedHash("before"), "governed");
        var failure = new PartialReplaceFileFailureBoundary(nativeErrorCode);
        var host = Host(paths, windowsReplacementBoundary: failure);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var firstDispatch = new RecordingDispatchBoundary();

        var exception = await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            firstDispatch));

        Assert.Contains(nativeErrorCode.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, failure.InvocationCount);
        Assert.Equal(1, firstDispatch.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var stage = Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var original = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Equal("governed", await File.ReadAllTextAsync(stage));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindOutcomeAsync("effect-alpha", "operation-alpha", 1));

        var isAmbiguousPublishedShape = nativeErrorCode == PartialReplaceFileFailureBoundary.UnableToMoveReplacement2;
        if (isAmbiguousPublishedShape)
        {
            var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Equal("before", await File.ReadAllTextAsync(displaced));
            Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Equal("before", await File.ReadAllTextAsync(path));
        }

        var restarted = Host(paths);
        var probe = await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Null(probe.AfterEvidenceId);
        var replayDispatch = new RecordingDispatchBoundary();
        // 1177 crossed native dispatch even though the target is absent; the authenticated retained
        // stage/original/displaced witness must therefore remain reconciliation-required.
        var replayException = await Assert.ThrowsAsync<IOException>(() => restarted.ExecuteAsync(Request(input, prepared.BeforeEvidence), replayDispatch));
        Assert.Contains("requires reconciliation", replayException.Message, StringComparison.Ordinal);
        Assert.Equal(0, replayDispatch.CrossCount);

        var clock = new MutableWorkspaceActionTimeProvider(TimeProvider.System.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(25));
        if (isAmbiguousPublishedShape)
        {
            await File.WriteAllTextAsync(path, "external");
            var cleanup = await Host(
                paths,
                timeProvider: clock,
                attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased)).CleanupOrphansAsync(1);
            Assert.Equal(0, cleanup);
            Assert.Equal("external", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
            Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        }
        else
        {
            var cleanup = await Host(
                paths,
                timeProvider: clock,
                attemptPresence: new FixedAttemptPresenceResolver(WorkspaceActionAttemptPresence.ArtifactReleased)).CleanupOrphansAsync(1);
            Assert.Equal(1, cleanup);
            Assert.Equal("before", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.original"));
            Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
        }
    }

    [Theory]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToRemoveReplaced, false)]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToRemoveReplaced, true)]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToMoveReplacement, false)]
    [InlineData(PartialReplaceFileFailureBoundary.UnableToMoveReplacement, true)]
    public async Task WindowsPartialReplaceFileCurrentTargetCorruptMarkerRequiresReconciliationWithoutRedispatch(int nativeErrorCode, bool tamperMarker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-partial-replace-current-target.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-partial-replace-current-target.txt", ExpectedHash("before"), "governed");
        var failure = new PartialReplaceFileFailureBoundary(nativeErrorCode);
        var host = Host(paths, windowsReplacementBoundary: failure);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var firstDispatch = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            firstDispatch));

        Assert.Equal(1, firstDispatch.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var stage = Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var marker = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        if (tamperMarker)
        {
            await File.WriteAllTextAsync(marker, "tampered authenticated marker");
        }
        else
        {
            File.Delete(marker);
        }

        var replayDispatch = new RecordingDispatchBoundary();
        var replayException = await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayDispatch));

        Assert.Contains("requires reconciliation", replayException.Message, StringComparison.Ordinal);
        Assert.Equal(0, replayDispatch.CrossCount);
        Assert.Equal("governed", await File.ReadAllTextAsync(stage));
    }

    [Theory]
    [InlineData("marker", false)]
    [InlineData("marker", true)]
    [InlineData("original", false)]
    [InlineData("displaced", false)]
    public async Task WindowsPartialReplaceFileCorruptWitnessRequiresReconciliationWithoutRedispatch(string artifact, bool tamperMarker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-partial-replace-corrupt.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-partial-replace-corrupt.txt", ExpectedHash("before"), "governed");
        var failure = new PartialReplaceFileFailureBoundary(PartialReplaceFileFailureBoundary.UnableToMoveReplacement2);
        var host = Host(paths, windowsReplacementBoundary: failure);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var firstDispatch = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            firstDispatch));

        Assert.Equal(1, firstDispatch.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var stage = Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var marker = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        var original = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        var displaced = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        switch (artifact)
        {
            case "marker" when tamperMarker:
                await File.WriteAllTextAsync(marker, "tampered authenticated marker");
                break;
            case "marker":
                File.Delete(marker);
                break;
            case "original":
                File.Delete(original);
                break;
            case "displaced":
                File.Delete(displaced);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(artifact), artifact, "Unsupported retained witness corruption case.");
        }

        var restarted = Host(paths);
        var replayDispatch = new RecordingDispatchBoundary();
        var replayException = await Assert.ThrowsAsync<IOException>(() => restarted.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayDispatch));

        Assert.Contains("requires reconciliation", replayException.Message, StringComparison.Ordinal);
        Assert.Equal(0, replayDispatch.CrossCount);
        Assert.Equal("governed", await File.ReadAllTextAsync(stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsPartialReplaceFileAppendCorruptMarkerRequiresReconciliationWithoutRedispatch(bool tamperMarker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-partial-replace-append.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Append, "notes/windows-partial-replace-append.txt", ExpectedHash("before"), "-after");
        var failure = new PartialReplaceFileFailureBoundary(PartialReplaceFileFailureBoundary.UnableToMoveReplacement2);
        var host = Host(paths, windowsReplacementBoundary: failure);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var firstDispatch = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            firstDispatch));

        Assert.Equal(1, firstDispatch.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var stage = Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var marker = Assert.Single(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.original"));
        Assert.Single(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        if (tamperMarker)
        {
            await File.WriteAllTextAsync(marker, "tampered authenticated marker");
        }
        else
        {
            File.Delete(marker);
        }

        var restarted = Host(paths);
        var replayDispatch = new RecordingDispatchBoundary();
        var replayException = await Assert.ThrowsAsync<IOException>(() => restarted.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayDispatch));

        Assert.Contains("requires reconciliation", replayException.Message, StringComparison.Ordinal);
        Assert.Equal(0, replayDispatch.CrossCount);
        Assert.Equal("before-after", await File.ReadAllTextAsync(stage));
    }

    [Theory]
    [InlineData("original", false)]
    [InlineData("original", true)]
    [InlineData("displaced", false)]
    [InlineData("displaced", true)]
    public async Task WindowsPartialReplaceFileAppendCorruptBeforeImageWitnessRequiresReconciliationWithoutRedispatch(string artifact, bool tamper)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-partial-replace-append-before-image.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Append, "notes/windows-partial-replace-append-before-image.txt", ExpectedHash("before"), "-after");
        var failure = new PartialReplaceFileFailureBoundary(PartialReplaceFileFailureBoundary.UnableToMoveReplacement2);
        var host = Host(paths, windowsReplacementBoundary: failure);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var firstDispatch = new RecordingDispatchBoundary();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            firstDispatch));

        Assert.Equal(1, firstDispatch.CrossCount);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        var stage = Assert.Single(Directory.EnumerateFiles(staging, "*.stage"));
        var beforeImage = Assert.Single(Directory.EnumerateFiles(staging, $"*.stage.{artifact}"));
        if (tamper)
        {
            await File.WriteAllTextAsync(beforeImage, "tampered");
        }
        else
        {
            File.Delete(beforeImage);
        }

        var restarted = Host(paths);
        var replayDispatch = new RecordingDispatchBoundary();
        var replayException = await Assert.ThrowsAsync<IOException>(() => restarted.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayDispatch));

        Assert.Contains("requires reconciliation", replayException.Message, StringComparison.Ordinal);
        Assert.Equal(0, replayDispatch.CrossCount);
        Assert.Equal("before-after", await File.ReadAllTextAsync(stage));
    }

    [Fact]
    public async Task WindowsUnrelatedMalformedStageMarkerDoesNotCreateFalsePositiveReconciliationWitness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-unrelated-malformed-marker.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/windows-unrelated-malformed-marker.txt", ExpectedHash("before"), "governed");
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        File.Delete(path);
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "stage-unrelated.stage.marker"), "malformed marker");
        var replayDispatch = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayDispatch);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, replayDispatch.CrossCount);
    }

    [Fact]
    public async Task ExistingWindowsReplacementRejectsCaseOnlyTargetAliasBeforePublishing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "windows-case-alias.txt");
        var alias = workspace.File("notes", "WINDOWS-CASE-ALIAS.TXT");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall)
            {
                File.Move(path, alias);
            }
        });
        var input = Input(WorkspaceActionKind.Write, "notes/windows-case-alias.txt", ExpectedHash("before"), "governed");
        var host = Host(paths, namespaceRaceObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("before", await File.ReadAllTextAsync(alias));
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(workspace.File("notes")),
            entry => string.Equals(Path.GetFileName(entry), Path.GetFileName(alias), StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(workspace.File("notes")),
            entry => string.Equals(Path.GetFileName(entry), "windows-case-alias.txt", StringComparison.Ordinal));
        var staging = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "staging");
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.marker"));
        Assert.Empty(Directory.EnumerateFiles(staging, "*.stage.displaced"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));
        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        var replayBoundary = new RecordingDispatchBoundary();
        Assert.Equal(
            WorkspaceActionNativeCommitStatus.DispatchNotStarted,
            (await Host(paths).ExecuteAsync(Request(input, prepared.BeforeEvidence), replayBoundary)).Status);
        Assert.Equal(0, replayBoundary.CrossCount);
    }

    [Fact]
    public async Task ExistingWriteAmbiguousPartialStateFailsClosedWithoutRedispatch()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "partial.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/partial.txt", ExpectedHash("before"), "complete-after-image");
        var host = Host(paths, new ThrowingDurabilityObserver());
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));
        await File.WriteAllTextAsync(path, "partial");

        var restarted = Host(paths);
        var probe = await restarted.ProbeAsync(Probe(input, prepared.BeforeEvidence));
        var replayBoundary = new RecordingDispatchBoundary();
        var replay = await restarted.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            replayBoundary);

        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
        Assert.Null(probe.AfterEvidenceId);
        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, replay.Status);
        Assert.Equal(0, replayBoundary.CrossCount);
        Assert.Equal("partial", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExpectedAbsentPublicationRacePreservesExternalWinner()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "publish-race.txt");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/publish-race.txt", ExpectedAbsent(), "governed");
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(WorkspaceActionCommitPoint.BeforeInstallTargetMutation, point);
            File.WriteAllText(path, "external");
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var boundary = new RecordingDispatchBoundary();

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            boundary));

        Assert.Equal(1, observer.Count);
        Assert.Equal(1, boundary.CrossCount);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Fact]
    public async Task SameContentReplacementAfterPreparationCannotReuseTheAdmittedPhysicalIdentity()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "replacement.txt");
        await File.WriteAllTextAsync(path, "same-content");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Delete, "notes/replacement.txt", ExpectedHash("same-content"));
        var host = Host(paths);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        File.Delete(path);
        await File.WriteAllTextAsync(path, "same-content");
        var boundary = new RecordingDispatchBoundary();

        var result = await host.ExecuteAsync(Request(input, prepared.BeforeEvidence), boundary);

        Assert.Equal(WorkspaceActionNativeCommitStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.CrossCount);
        Assert.Equal("same-content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExistingUnixWriteRaceRestoresExternalReplacementWithoutOverwritingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "write-race.txt");
        var original = workspace.File("notes", "write-race.original");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/write-race.txt", ExpectedHash("before"), "governed");
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(WorkspaceActionCommitPoint.BeforeInstallTargetMutation, point);
            File.Move(path, original);
            File.WriteAllText(path, "external");
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Fact]
    public async Task ExistingUnixWriteExchangeRaceNeverRollsBackOverASecondExternalWinner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "double-race.txt");
        var original = workspace.File("notes", "double-race.original");
        var governed = workspace.File("notes", "double-race.governed");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point == WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall)
            {
                File.Move(path, original);
                File.WriteAllText(path, "first-external");
            }
            else if (point == WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall)
            {
                File.Move(path, governed);
                File.WriteAllText(path, "second-external");
            }
        });
        var host = Host(paths, namespaceRaceObserver: observer);
        var input = Input(WorkspaceActionKind.Write, "notes/double-race.txt", ExpectedHash("before"), "governed");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal(
            [WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall, WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall],
            observer.Points);
        Assert.Equal("second-external", await File.ReadAllTextAsync(path));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.Equal("governed", await File.ReadAllTextAsync(governed));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write)]
    [InlineData(WorkspaceActionKind.Delete)]
    public async Task UnixPostSystemCallAncestorSwapCannotProduceConclusiveOutcome(WorkspaceActionKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        var ancestor = workspace.File("notes");
        var detached = workspace.File("notes-detached");
        Directory.CreateDirectory(ancestor);
        var path = Path.Combine(ancestor, "post-ancestor.txt");
        if (kind == WorkspaceActionKind.Delete)
        {
            await File.WriteAllTextAsync(path, "before");
        }
        var paths = new WorkspacePaths(workspace.RootPath);
        var afterPoint = kind == WorkspaceActionKind.Delete
            ? WorkspaceActionNamespaceRacePoint.AfterDeleteSystemCall
            : WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall;
        var observer = new CallbackNamespaceRaceObserver(point =>
        {
            if (point != afterPoint)
            {
                return;
            }
            Directory.Move(ancestor, detached);
            Directory.CreateDirectory(ancestor);
        });
        var host = Host(paths, namespaceRaceObserver: observer);
        var input = kind == WorkspaceActionKind.Delete
            ? Input(kind, "notes/post-ancestor.txt", ExpectedHash("before"))
            : Input(kind, "notes/post-ancestor.txt", ExpectedAbsent(), "governed");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.False(File.Exists(path));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        if (kind == WorkspaceActionKind.Write)
        {
            Assert.Equal("governed", await File.ReadAllTextAsync(Path.Combine(detached, "post-ancestor.txt")));
        }
    }

    [Fact]
    public async Task ExistingUnixDeleteRaceRestoresExternalReplacementWithoutQuarantiningIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "delete-race.txt");
        var original = workspace.File("notes", "delete-race.original");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Delete, "notes/delete-race.txt", ExpectedHash("before"));
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(WorkspaceActionCommitPoint.BeforeDeleteNamespaceMutation, point);
            File.Move(path, original);
            File.WriteAllText(path, "external");
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
        Assert.Empty(Directory.EnumerateFiles(quarantine, "*.payload"));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write)]
    [InlineData(WorkspaceActionKind.Delete)]
    public async Task ExistingUnixInPlaceChangeAtCommitIsRestoredWithoutStaleMutation(WorkspaceActionKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "in-place-race.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(kind, "notes/in-place-race.txt", ExpectedHash("before"), kind == WorkspaceActionKind.Write ? "governed" : null);
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(
                kind == WorkspaceActionKind.Delete
                    ? WorkspaceActionCommitPoint.BeforeDeleteNamespaceMutation
                    : WorkspaceActionCommitPoint.BeforeInstallTargetMutation,
                point);
            File.WriteAllText(path, "external");
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
        if (kind == WorkspaceActionKind.Delete)
        {
            var quarantine = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions", "quarantine");
            Assert.Empty(Directory.EnumerateFiles(quarantine, "*.payload"));
        }
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Write)]
    [InlineData(WorkspaceActionKind.Delete)]
    public async Task AncestorNamespaceSwapAtCommitBoundaryMutatesNeitherTree(WorkspaceActionKind kind)
    {
        using var workspace = new TestWorkspace();
        var ancestor = workspace.File("notes");
        var detached = workspace.File("notes-original");
        Directory.CreateDirectory(ancestor);
        var target = workspace.File("notes", "ancestor-race.txt");
        if (kind == WorkspaceActionKind.Delete)
        {
            await File.WriteAllTextAsync(target, "before");
        }
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = kind == WorkspaceActionKind.Delete
            ? Input(kind, "notes/ancestor-race.txt", ExpectedHash("before"))
            : Input(kind, "notes/ancestor-race.txt", ExpectedAbsent(), "governed");
        var ancestorMoveBlocked = false;
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(
                kind == WorkspaceActionKind.Delete
                    ? WorkspaceActionCommitPoint.BeforeDeleteNamespaceMutation
                    : WorkspaceActionCommitPoint.BeforeInstallTargetMutation,
                point);
            try
            {
                Directory.Move(ancestor, detached);
                Directory.CreateDirectory(ancestor);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && kind == WorkspaceActionKind.Delete
                && exception is IOException or UnauthorizedAccessException)
            {
                ancestorMoveBlocked = !Directory.Exists(detached);
                throw new IOException("The Windows retained ancestor fence blocked the namespace swap.", exception);
            }
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        await Assert.ThrowsAnyAsync<IOException>(() => host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary()));

        if (ancestorMoveBlocked)
        {
            Assert.Equal("before", await File.ReadAllTextAsync(target));
            Assert.False(Directory.Exists(detached));
            Assert.False(File.Exists(Path.Combine(detached, "ancestor-race.txt")));
            Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
            return;
        }

        Assert.False(File.Exists(target));
        var detachedTarget = Path.Combine(detached, "ancestor-race.txt");
        Assert.Equal(kind == WorkspaceActionKind.Delete, File.Exists(detachedTarget));
        if (kind == WorkspaceActionKind.Delete)
        {
            Assert.Equal("before", await File.ReadAllTextAsync(detachedTarget));
        }
        Assert.Null(await new WorkspaceActionEvidenceStore(paths).FindAfterAsync("effect-alpha", "operation-alpha", 1));
    }

    [Fact]
    public async Task WindowsDeleteRetainedHandleBlocksExternalWriteAndNamespaceReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var path = workspace.File("notes", "delete-fenced.txt");
        var replacement = workspace.File("notes", "delete-fenced.replacement");
        await File.WriteAllTextAsync(path, "before");
        await File.WriteAllTextAsync(replacement, "external");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Delete, "notes/delete-fenced.txt", ExpectedHash("before"));
        var writeBlocked = false;
        var replacementBlocked = false;
        var observer = new CallbackCommitObserver((point, _) =>
        {
            Assert.Equal(WorkspaceActionCommitPoint.BeforeDeleteNamespaceMutation, point);
            try
            {
                File.WriteAllText(path, "external-write");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeBlocked = true;
            }
            try
            {
                File.Move(replacement, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                replacementBlocked = true;
            }
        });
        var host = Host(paths, commitObserver: observer);
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var result = await host.ExecuteAsync(
            Request(input, prepared.BeforeEvidence),
            new RecordingDispatchBoundary());

        Assert.True(writeBlocked);
        Assert.True(replacementBlocked);
        Assert.Equal(WorkspaceActionNativeCommitStatus.OutcomeObserved, result.Status);
        Assert.False(File.Exists(path));
        Assert.Equal("external", await File.ReadAllTextAsync(replacement));
    }

    [Fact]
    public async Task TwoExpectedAbsentOperationsAgainstOneStateCannotBothCommit()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "notes/race.txt", ExpectedAbsent(), "winner");
        var host = Host(paths);
        var first = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));
        var second = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(input));

        var results = await Task.WhenAll(
            host.ExecuteAsync(Request(input, first.BeforeEvidence, "effect-first", "operation-first"), new RecordingDispatchBoundary()),
            host.ExecuteAsync(Request(input, second.BeforeEvidence, "effect-second", "operation-second"), new RecordingDispatchBoundary()));

        Assert.Single(results, result => result.Status == WorkspaceActionNativeCommitStatus.OutcomeObserved);
        Assert.Single(results, result => result.Status == WorkspaceActionNativeCommitStatus.DispatchNotStarted);
        Assert.Equal("winner", await File.ReadAllTextAsync(workspace.File("notes", "race.txt")));
    }

    [Fact]
    public async Task SymlinkAncestorSymlinkTargetAndSpecialFileAreRejected()
    {
        using var workspace = new TestWorkspace();
        var host = Host(new WorkspacePaths(workspace.RootPath));
        Directory.CreateDirectory(workspace.File("real"));
        await File.WriteAllTextAsync(workspace.File("real", "value.txt"), "before");
        try
        {
            Directory.CreateSymbolicLink(workspace.File("linked"), workspace.File("real"));
            File.CreateSymbolicLink(workspace.File("target-link.txt"), workspace.File("real", "value.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.ThrowsAny<IOException>(() => host.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "linked/value.txt",
            ExpectedHash("before"),
            "after")).GetAwaiter().GetResult());
        Assert.ThrowsAny<IOException>(() => host.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "target-link.txt",
            ExpectedHash("before"),
            "after")).GetAwaiter().GetResult());

        if (!OperatingSystem.IsWindows())
        {
            var fifo = workspace.File("fifo");
            if (UnixMkFifo(fifo, 0x180) == 0)
            {
                Assert.ThrowsAny<IOException>(() => host.PrepareAsync(Input(
                    WorkspaceActionKind.Write,
                    "fifo",
                    ExpectedHash("before"),
                    "after")).GetAwaiter().GetResult());
            }
        }
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.File("real", "value.txt")));
    }

    [Fact]
    public async Task HostEquivalentTextualAliasIsRejectedAfterFirstNativeIdentityAdmission()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        await File.WriteAllTextAsync(workspace.File("notes", "case.txt"), "before");
        var host = Host(new WorkspacePaths(workspace.RootPath));
        var canonical = Input(WorkspaceActionKind.Write, "notes/case.txt", ExpectedHash("before"), "after");
        Assert.NotNull(await host.PrepareAsync(canonical));
        WorkspaceActionInput alias;
        try
        {
            alias = Input(WorkspaceActionKind.Write, "NOTES/CASE.TXT", ExpectedHash("before"), "after");
            Assert.Null(await host.PrepareAsync(alias));
        }
        catch (IOException)
        {
            // A case-sensitive macOS volume does not expose the two spellings as one native identity.
        }
    }

    [Fact]
    public async Task ProbeProjectsHostEquivalentAncestorAliasAsIndeterminate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        var original = workspace.File("NOTES");
        var temporary = workspace.File("notes-temporary");
        var alias = workspace.File("notes");
        Directory.CreateDirectory(original);
        await File.WriteAllTextAsync(workspace.File("NOTES", "value.txt"), "before");
        var paths = new WorkspacePaths(workspace.RootPath);
        var input = Input(WorkspaceActionKind.Write, "NOTES/value.txt", ExpectedHash("before"), "after");
        var prepared = Assert.IsType<WorkspaceActionNativePreparation>(await Host(paths).PrepareAsync(input));

        Directory.Move(original, temporary);
        Directory.Move(temporary, alias);
        if (!Directory.Exists(original))
        {
            // A case-sensitive Windows volume does not expose the two spellings as one native identity.
            return;
        }

        var probe = await Host(paths).ProbeAsync(Probe(input, prepared.BeforeEvidence));

        Assert.Equal(WorkspaceActionReconciliationPosture.Indeterminate, probe.Posture);
    }

    [Fact]
    public async Task HostEquivalentAncestorAliasIsRejectedBeforePermissionEvaluation()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("sensitive"));
        await File.WriteAllTextAsync(workspace.File("sensitive", "value.txt"), "before");
        if (!Directory.Exists(workspace.File("SENSITIVE")))
        {
            // The current volume does not resolve this alternate spelling as a host alias.
            return;
        }
        var permission = new MutablePermissionRevalidator(FileSystemOperation.Modify);
        var host = Host(new WorkspacePaths(workspace.RootPath), permissionRevalidator: permission);

        await Assert.ThrowsAnyAsync<IOException>(() => host.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "SENSITIVE/value.txt",
            ExpectedHash("before"),
            "after")));

        Assert.Empty(permission.Operations);
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.File("sensitive", "value.txt")));
    }

    [Fact]
    public async Task Case_sensitive_directory_retains_distinct_target_spellings_as_distinct_evidence_identities()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("notes"));
        var lowerPath = workspace.File("notes", "distinct.txt");
        var upperPath = workspace.File("notes", "DISTINCT.TXT");
        await File.WriteAllTextAsync(lowerPath, "lower");
        await File.WriteAllTextAsync(upperPath, "upper");
        if (!string.Equals(await File.ReadAllTextAsync(lowerPath), "lower", StringComparison.Ordinal)
            || !string.Equals(await File.ReadAllTextAsync(upperPath), "upper", StringComparison.Ordinal))
        {
            // The current volume is case-insensitive and cannot exercise this posture.
            return;
        }
        var host = Host(new WorkspacePaths(workspace.RootPath));

        var lower = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/distinct.txt",
            ExpectedHash("lower"),
            "lower-after")));
        var upper = Assert.IsType<WorkspaceActionNativePreparation>(await host.PrepareAsync(Input(
            WorkspaceActionKind.Write,
            "notes/DISTINCT.TXT",
            ExpectedHash("upper"),
            "upper-after")));

        Assert.NotEqual(lower.BeforeEvidence.TargetFingerprint, upper.BeforeEvidence.TargetFingerprint);
    }

    private static WorkspaceActionNativeHost Host(
        WorkspacePaths paths,
        IWorkspaceActionDurabilityObserver? observer = null,
        IWorkspaceActionCommitObserver? commitObserver = null,
        WorkspaceActionStorageLimits? quota = null,
        IWorkspaceActionCommittedAfterEvidenceResolver? committedAfterEvidence = null,
        TimeProvider? timeProvider = null,
        IWorkspaceActionAttemptPresenceResolver? attemptPresence = null,
        WorkspaceActionEvidenceStore? evidenceStore = null,
        IWorkspaceActionPermissionRevalidator? permissionRevalidator = null,
        IWorkspaceActionNamespaceRaceObserver? namespaceRaceObserver = null,
        IWorkspaceActionWindowsReplacementBoundary? windowsReplacementBoundary = null)
    {
        WorkspaceActionScopeId.TryParse("workspace", out var scope);
        return new WorkspaceActionNativeHost(
            paths,
            scope!,
            new ImmediateWorkspaceMutationCommitBoundary(),
            permissionRevalidator ?? new FixedPermissionRevalidator(),
            evidenceStore: evidenceStore,
            timeProvider: timeProvider,
            durabilityObserver: observer,
            commitObserver: commitObserver,
            namespaceRaceObserver: namespaceRaceObserver,
            quota: quota,
            committedAfterEvidence: committedAfterEvidence,
            attemptPresence: attemptPresence,
            windowsReplacementBoundary: windowsReplacementBoundary);
    }

    private static WorkspaceActionInput Input(
        WorkspaceActionKind kind,
        string relativeTarget,
        WorkspaceActionPrecondition precondition,
        string? literal = null)
    {
        WorkspaceActionScopeId.TryParse("workspace", out var scope);
        WorkspaceRelativeFileTarget.TryParse(relativeTarget, out var target, out var reason);
        Assert.Null(reason);
        var segments = literal is null
            ? Array.Empty<WorkspaceActionContentSegment>()
            : [new WorkspaceActionContentSegment(WorkspaceActionContentSegmentKind.LiteralUtf8, literal, null)];
        var input = new WorkspaceActionInput(WorkspaceActionContractLimits.CurrentSchemaVersion, kind, scope!, target!, precondition, segments);
        var canonical = WorkspaceActionInputContract.Encode(input);
        Assert.True(WorkspaceActionInputContract.TryParse(canonical, kind, out var captured, out reason), reason);
        Assert.Equal(scope, captured!.ScopeId);
        return input;
    }

    private static WorkspaceActionPrecondition ExpectedAbsent()
        => new(WorkspaceActionPreconditionKind.ExpectedAbsent, null, null, null, null);

    private static WorkspaceActionPrecondition ExpectedHash(string value)
        => new(WorkspaceActionPreconditionKind.ExpectedContentHash, Sha256(value), null, null, null);

    private static WorkspaceActionNativeExecutionRequest Request(WorkspaceActionInput input, WorkspaceActionBeforeEvidence before)
        => new(input, before.TargetFingerprint, before.EvidenceId, "effect-alpha", "operation-alpha", 1);

    private static WorkspaceActionNativeExecutionRequest Request(
        WorkspaceActionInput input,
        WorkspaceActionBeforeEvidence before,
        string effectId,
        string operationId)
        => new(input, before.TargetFingerprint, before.EvidenceId, effectId, operationId, 1);

    private static WorkspaceActionReconciliationProbeRequest Probe(
        WorkspaceActionInput input,
        WorkspaceActionBeforeEvidence before)
        => new(input, before.TargetFingerprint, before.EvidenceId, "effect-alpha", "operation-alpha", 1);

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserPrivateDirectorySecurity(string path)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        AssertCurrentUserPrivateSecurity(
            security,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserPrivateFileSecurity(string path)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        AssertCurrentUserPrivateSecurity(security, InheritanceFlags.None);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserPrivateSecurity(
        FileSystemSecurity security,
        InheritanceFlags expectedInheritance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();

        Assert.NotNull(currentUser);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(currentUser, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(rules);
        Assert.False(rule.IsInherited);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.Equal(expectedInheritance, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureControlledWindowsReplacementSecurity(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows identity is required for replacement metadata testing.");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureInheritedWindowsReplacementParentSecurity(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows identity is required for inherited replacement metadata testing.");
        var directory = new DirectoryInfo(path);
        var security = FileSystemAclExtensions.GetAccessControl(directory);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(directory, security);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CaptureWindowsSecurityDescriptor(string path)
        => FileSystemAclExtensions.GetAccessControl(new FileInfo(path)).GetSecurityDescriptorBinaryForm();

    [SupportedOSPlatform("windows")]
    private static void AssertSameWindowsReplacementSecurity(byte[] expected, byte[] actual)
    {
        var expectedDescriptor = new RawSecurityDescriptor(expected, 0);
        var actualDescriptor = new RawSecurityDescriptor(actual, 0);
        Assert.Equal(expectedDescriptor.Owner, actualDescriptor.Owner);
        Assert.Equal(expectedDescriptor.Group, actualDescriptor.Group);
        Assert.Equal(
            expectedDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected),
            actualDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        var expectedAccessControlList = expectedDescriptor.DiscretionaryAcl;
        var actualAccessControlList = actualDescriptor.DiscretionaryAcl;
        Assert.NotNull(expectedAccessControlList);
        Assert.NotNull(actualAccessControlList);
        Assert.Equal(GetBinaryForm(expectedAccessControlList), GetBinaryForm(actualAccessControlList));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetBinaryForm(GenericAcl accessControlList)
    {
        var bytes = new byte[accessControlList.BinaryLength];
        accessControlList.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static void AssertInheritedUnprotectedWindowsAccessControl(byte[] securityDescriptor)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows identity is required for inherited replacement metadata testing.");
        var descriptor = new RawSecurityDescriptor(securityDescriptor, 0);
        Assert.False(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        var accessControlList = Assert.IsType<RawAcl>(descriptor.DiscretionaryAcl);
        Assert.Contains(
            accessControlList.Cast<GenericAce>(),
            accessControlEntry => accessControlEntry is CommonAce commonAccessControlEntry
                && commonAccessControlEntry.IsInherited
                && currentUser.Equals(commonAccessControlEntry.SecurityIdentifier));
    }

    private static bool TryCreateHardLink(string alias, string source)
        => OperatingSystem.IsWindows()
            ? CreateHardLink(alias, source, IntPtr.Zero)
            : UnixLink(source, alias) == 0;

    private static string[] AccessControlLines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native metadata verification process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var combined = await output + await error;
        return (process.ExitCode, combined);
    }

    private static async Task<string> RunCrashWorkerAsync(
        string rootPath,
        WorkspaceActionInput input,
        WorkspaceActionBeforeEvidence before,
        bool exitBeforeMutation,
        bool exitAfterWindowsReplacementSystemCall = false)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(
            startInfo,
            typeof(WorkspaceActionNativeHostTests).Assembly.Location,
            $"{typeof(WorkspaceActionNativeHostTests).FullName}.{nameof(WorkspaceActionCrashWorker)}");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WorkerRootVariable] = rootPath;
        startInfo.Environment[WorkerBeforeVariable] = before.EvidenceId;
        startInfo.Environment[WorkerTargetVariable] = before.TargetFingerprint;
        startInfo.Environment[WorkerInputVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkspaceActionInputContract.Encode(input)));
        startInfo.Environment[WorkerKindVariable] = input.Kind.ToString();
        var failpointMarker = Path.Combine(rootPath, $"workspace-action-crash-failpoint-{Guid.NewGuid():N}.marker");
        startInfo.Environment[WorkerFailpointMarkerVariable] = failpointMarker;
        startInfo.Environment[WorkerExitBeforeMutationVariable] = exitBeforeMutation ? "1" : "0";
        startInfo.Environment[WorkerExitAfterWindowsReplacementSystemCallVariable] = exitAfterWindowsReplacementSystemCall ? "1" : "0";
        using var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("The workspace action crash worker did not start.");
        var output = worker.StandardOutput.ReadToEndAsync();
        var error = worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, worker.ExitCode);
        _ = await output;
        var stderr = await error;
        AssertCrashWorkerReachedFailpoint(failpointMarker);
        return stderr;
    }

    private static void AssertCrashWorkerReachedFailpoint(string markerPath)
    {
        Assert.True(File.Exists(markerPath));
        Assert.Equal("reached", File.ReadAllText(markerPath));
    }

    private static void WriteAllTextWithCompatibleSharing(string path, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteCrashWorkerFailpointMarker(string markerPath)
    {
        using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write("reached"u8);
        stream.Flush(flushToDisk: true);
    }

    private static bool TrySetLinuxDefaultAcl(string path)
    {
        byte[] value =
        [
            2, 0, 0, 0,
            1, 0, 7, 0, 255, 255, 255, 255,
            4, 0, 7, 0, 255, 255, 255, 255,
            32, 0, 7, 0, 255, 255, 255, 255,
        ];
        var buffer = Marshal.AllocHGlobal(value.Length);
        try
        {
            Marshal.Copy(value, 0, buffer, value.Length);
            if (LinuxSetXattr(path, "system.posix_acl_default", buffer, checked((nuint)value.Length), 0) == 0)
            {
                return true;
            }
            var error = Marshal.GetLastPInvokeError();
            return error is 1 or 13 or 95
                ? false
                : throw new IOException($"setxattr failed with native error {error}.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool HasLinuxExtendedAttribute(string path, string name)
    {
        if (LinuxGetXattr(path, name, IntPtr.Zero, 0) >= 0)
        {
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        return error == 61
            ? false
            : throw new IOException($"getxattr failed with native error {error}.");
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int LinuxSetXattr(string path, string name, IntPtr value, nuint size, int flags);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint LinuxGetXattr(string path, string name, IntPtr value, nuint size);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int UnixMkFifo(string path, int mode);

    private sealed class ImmediateWorkspaceMutationCommitBoundary : IWorkspaceMutationCommitBoundary
    {
        public Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
        {
            Assert.Single(affectedPaths);
            cancellationToken.ThrowIfCancellationRequested();
            return commit(cancellationToken);
        }
    }

    private sealed class FixedPermissionRevalidator : IWorkspaceActionPermissionRevalidator
    {
        public Task<WorkspaceActionPermissionRevalidation> RevalidateAsync(
            WorkspaceActionPermissionRevalidationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkspaceActionPermissionRevalidation(true, request.Operation, Sha256("permission-policy")));
        }
    }

    private sealed class MutablePermissionRevalidator(params FileSystemOperation[] allowed) : IWorkspaceActionPermissionRevalidator
    {
        private readonly HashSet<FileSystemOperation> _allowed = allowed.ToHashSet();

        public List<FileSystemOperation> Operations { get; } = [];

        public string PolicyHash { get; set; } = Sha256("permission-policy");

        public Task<WorkspaceActionPermissionRevalidation> RevalidateAsync(
            WorkspaceActionPermissionRevalidationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(request.Operation);
            return Task.FromResult(new WorkspaceActionPermissionRevalidation(
                _allowed.Contains(request.Operation),
                request.Operation,
                PolicyHash));
        }
    }

    private sealed class CallbackNamespaceRaceObserver(Action<WorkspaceActionNamespaceRacePoint> callback) : IWorkspaceActionNamespaceRaceObserver
    {
        private readonly Action<WorkspaceActionNamespaceRacePoint> _callback = callback;

        public List<WorkspaceActionNamespaceRacePoint> Points { get; } = [];

        public Task ObserveAsync(
            WorkspaceActionNamespaceRacePoint point,
            string beforeEvidenceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.StartsWith("before-", beforeEvidenceId, StringComparison.Ordinal);
            Points.Add(point);
            _callback(point);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatchBoundary : IWorkspaceActionNativeDispatchBoundary
    {
        public int CrossCount { get; private set; }

        public Task<WorkspaceActionNativeOutcome> CrossAsync(
            Func<CancellationToken, Task<WorkspaceActionNativeOutcome>> callback,
            CancellationToken cancellationToken = default)
        {
            CrossCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return callback(cancellationToken);
        }
    }

    private sealed class ThrowingDurabilityObserver(WorkspaceActionDurabilityPoint? throwPoint = null) : IWorkspaceActionDurabilityObserver
    {
        public WorkspaceActionDurabilityPoint Point { get; private set; }

        public Task ObserveAsync(WorkspaceActionDurabilityPoint point, string beforeEvidenceId, string effectId, CancellationToken cancellationToken = default)
        {
            Point = point;
            if (throwPoint is null || throwPoint == point)
            {
                throw new IOException("Injected after-publication evidence failure.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackCommitObserver(Action<WorkspaceActionCommitPoint, string> callback) : IWorkspaceActionCommitObserver
    {
        private readonly Action<WorkspaceActionCommitPoint, string> _callback = callback;

        public int Count { get; private set; }

        public Task ObserveAsync(WorkspaceActionCommitPoint point, string beforeEvidenceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            _callback(point, beforeEvidenceId);
            return Task.CompletedTask;
        }
    }

    private sealed class ExitDurabilityObserver(string markerPath) : IWorkspaceActionDurabilityObserver
    {
        public Task ObserveAsync(WorkspaceActionDurabilityPoint point, string beforeEvidenceId, string effectId, CancellationToken cancellationToken = default)
        {
            WriteCrashWorkerFailpointMarker(markerPath);
            Environment.FailFast("aborted after workspace namespace mutation");
            throw new UnreachableException();
        }
    }

    private sealed class ExitCommitObserver(string markerPath) : IWorkspaceActionCommitObserver
    {
        public Task ObserveAsync(WorkspaceActionCommitPoint point, string beforeEvidenceId, CancellationToken cancellationToken = default)
        {
            WriteCrashWorkerFailpointMarker(markerPath);
            Environment.FailFast("aborted after private staging and before workspace namespace mutation");
            throw new UnreachableException();
        }
    }

    private sealed class ExitNamespaceRaceObserver(string markerPath) : IWorkspaceActionNamespaceRaceObserver
    {
        public Task ObserveAsync(WorkspaceActionNamespaceRacePoint point, string beforeEvidenceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point != WorkspaceActionNamespaceRacePoint.AfterWindowsReplacementSystemCallBeforeBackupRetention)
            {
                return Task.CompletedTask;
            }
            WriteCrashWorkerFailpointMarker(markerPath);
            Environment.FailFast("aborted after atomic replacement and before private backup retention");
            throw new UnreachableException();
        }
    }

    private sealed class RecordingCommittedAfterEvidenceResolver : IWorkspaceActionCommittedAfterEvidenceResolver
    {
        public (string EffectId, string OperationId, long Generation, string AfterEvidenceId, string AfterEvidenceHash)? Request { get; private set; }

        public Task<bool> IsCommittedAsync(
            string effectId,
            string idempotencyOperationId,
            long effectGeneration,
            string afterEvidenceId,
            string afterEvidenceHash,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = (effectId, idempotencyOperationId, effectGeneration, afterEvidenceId, afterEvidenceHash);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedAttemptPresenceResolver(
        WorkspaceActionAttemptPresence presence,
        WorkspaceActionPreparationCleanupResult? cleanupResult = null) : IWorkspaceActionAttemptPresenceResolver
    {
        public int ResolveCount { get; private set; }

        public List<string> ResolvedBeforeEvidenceIds { get; } = [];

        public List<IReadOnlyList<string>> PreparationBatches { get; } = [];

        public bool ThrowOnPreparationCleanup { get; init; }

        public Task<WorkspaceActionAttemptPresence> ResolveAsync(
            string effectId,
            string idempotencyOperationId,
            long effectGeneration,
            string beforeEvidenceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            ResolvedBeforeEvidenceIds.Add(beforeEvidenceId);
            return Task.FromResult(presence);
        }

        public Task<WorkspaceActionPreparationCleanupResult> TryCleanupPreparationsAsync(
            IReadOnlyList<WorkspaceActionBeforeEvidence> beforeEvidence,
            int maximumRemovals,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(beforeEvidence);
            cancellationToken.ThrowIfCancellationRequested();
            PreparationBatches.Add(beforeEvidence.Select(candidate => candidate.EvidenceId).ToArray());
            if (ThrowOnPreparationCleanup)
            {
                throw new IOException("Injected preparation cleanup interruption.");
            }
            return Task.FromResult(cleanupResult ?? WorkspaceActionPreparationCleanupResult.Unknown);
        }
    }

    private sealed class MutableWorkspaceActionTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
