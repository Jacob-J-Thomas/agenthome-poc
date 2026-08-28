using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.EffectAttempts;

public sealed class GovernedLoopEffectAttemptStoreTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T18:00:00Z");

    [Fact]
    public async Task Slash_bearing_operation_metadata_persists_by_safe_hash_and_exact_restart_reclaims_owner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);

        var created = await store.BeginAsync(prepared);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        Assert.Equal(prepared, created.Attempt);
        Assert.NotNull(created.Lease);
        var files = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath)
            .Select(Path.GetFileName)
            .Where(name => name != ".custom-loop-mutations.lock")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, files.Length);
        Assert.All(files, name => Assert.DoesNotContain("/", name!, StringComparison.Ordinal));
        Assert.All(files, name => Assert.Matches("^[0-9a-f]{64}(\\.[0-9a-f]{64}\\.json|\\.head|\\.owner)$", name!));

        var record = await File.ReadAllTextAsync(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json").Single());
        Assert.Contains("probe/observe", record, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-value", record, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialValue", record, StringComparison.OrdinalIgnoreCase);

        var concurrent = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, concurrent.Status);
        Assert.Null(concurrent.Lease);
        created.Lease!.Dispose();

        var restarted = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, restarted.Status);
        Assert.Equal(prepared.ContentHash, restarted.Attempt!.ContentHash);
        Assert.NotNull(restarted.Lease);
        restarted.Lease!.Dispose();
    }

    [Fact]
    public async Task Direct_successors_are_append_only_and_stale_expected_replay_conflicts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        var lease = Assert.IsAssignableFrom<IGovernedLoopEffectAttemptLease>(begun.Lease);
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
            "probe-outcome",
            "probe-after",
            _now.AddSeconds(3));
        var committed = GovernedLoopEffectAttemptContract.Advance(
            observed,
            GovernedLoopEffectPhase.Committed,
            GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectEvidenceStatus.Complete,
            "probe-outcome",
            "probe-after",
            _now.AddSeconds(4));

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.ContentHash, authorized, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.ContentHash, crossed, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(crossed.ContentHash, observed, lease)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(observed.ContentHash, committed, lease)).Status);
        Assert.Equal(5, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json").Count());
        Assert.All(
            new[] { prepared, authorized, crossed, observed, committed },
            version => Assert.Contains(
                Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json"),
                path => Path.GetFileName(path).Contains(version.ContentHash, StringComparison.Ordinal)));

        var staleReplay = await store.CompareExchangeAsync(observed.ContentHash, committed, lease);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Conflict, staleReplay.Status);
        Assert.Equal(committed.ContentHash, staleReplay.Attempt!.ContentHash);
        var exactReplay = await store.CompareExchangeAsync(committed.ContentHash, committed, lease);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, exactReplay.Status);
        lease.Dispose();

        var terminalReplay = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, terminalReplay.Status);
        Assert.Equal(committed.ContentHash, terminalReplay.Attempt!.ContentHash);
        Assert.Null(terminalReplay.Lease);
    }

    [Fact]
    public async Task Changed_intent_wrong_owner_and_illegal_successor_fail_closed_without_advancing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        var lease = begun.Lease!;

        var changed = Prepare(inputFingerprint: Hash('9'));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Conflict, (await new GovernedLoopEffectAttemptStore(paths).BeginAsync(changed)).Status);

        using var otherWorkspace = new TestWorkspace();
        var otherStore = new GovernedLoopEffectAttemptStore(new WorkspacePaths(otherWorkspace.RootPath));
        var otherBegun = await otherStore.BeginAsync(prepared);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Conflict, (await store.CompareExchangeAsync(prepared.ContentHash, authorized, otherBegun.Lease!)).Status);

        var tampered = authorized with { PreviousContentHash = Hash('f') };
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await store.CompareExchangeAsync(prepared.ContentHash, tampered, lease)).Status);
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json"));
        otherBegun.Lease!.Dispose();
        lease.Dispose();
    }

    [Fact]
    public async Task Headless_first_intent_recovers_but_interrupted_temp_is_preserved_and_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var created = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        created.Lease!.Dispose();
        File.Delete(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single());

        var recovered = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, recovered.Status);
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
        recovered.Lease!.Dispose();

        var head = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single();
        var interrupted = Path.Combine(
            paths.GovernedLoopEffectAttemptsPath,
            $".{Path.GetFileName(head)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(interrupted, prepared.ContentHash);
        var refused = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, refused.Status);
        Assert.True(File.Exists(interrupted));
    }

    [Fact]
    public async Task Read_only_current_head_never_acquires_an_owner_or_repairs_a_missing_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var missing = await ((IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths)).ReadAsync(workspaceId, "effect-operation-1", 1);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Missing, missing.Status);
        Assert.False(Directory.Exists(paths.GovernedLoopEffectAttemptsPath));

        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        begun.Lease!.Dispose();

        var before = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();
        var read = await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, read.Status);
        Assert.Equal(prepared.ContentHash, read.Attempt?.ContentHash);
        Assert.Equal(before, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());

        File.Delete(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single());
        var headless = await ((IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths)).ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, headless.Status);
        Assert.Empty(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
    }

    [Fact]
    public async Task Read_only_current_head_rejects_invalid_or_foreign_workspaces_without_touching_persistence()
    {
        using var workspace = new TestWorkspace();
        using var foreignWorkspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var foreignWorkspaceId = CapabilityWorkspaceScopeId.Create(foreignWorkspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);

        var invalid = await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync("invalid-workspace", "effect-operation-1", 1);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Unavailable, invalid.Status);
        Assert.False(Directory.Exists(paths.GovernedLoopEffectAttemptsPath));

        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        begun.Lease!.Dispose();
        var before = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();

        var rejected = await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync(foreignWorkspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Unavailable, rejected.Status);
        Assert.Equal(before, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, (await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration)).Status);
    }

    [Fact]
    public async Task Read_only_current_head_closes_invalid_coordinates_missing_attempts_and_cancelled_reads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var store = (IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths);

        var invalidOperation = await store.ReadAsync(workspaceId, "INVALID OPERATION", 1);
        var invalidGeneration = await store.ReadAsync(workspaceId, "effect-operation-1", 0);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, invalidOperation.Status);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, invalidGeneration.Status);
        Assert.False(Directory.Exists(paths.GovernedLoopEffectAttemptsPath));

        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await File.WriteAllBytesAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"), []);
        var absent = await store.ReadAsync(workspaceId, "effect-operation-1", 1);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Missing, absent.Status);
        var beforeCancellation = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(workspaceId, "effect-operation-1", 1, cancellation.Token));
        Assert.Equal(beforeCancellation, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());
    }

    [Fact]
    public async Task Read_only_current_head_rejects_initialized_directories_without_a_mutation_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);

        var result = await ((IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths)).ReadAsync(CapabilityWorkspaceScopeId.Create(paths.RootPath), "effect-operation-1", 1);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.GovernedLoopEffectAttemptsPath));
    }

    [Fact]
    public async Task Read_only_current_head_fails_closed_while_the_mutation_lock_is_exclusively_held()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        begun.Lease!.Dispose();
        var before = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();

        await using var externalLock = new FileStream(
            Path.Combine(paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var result = await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Unavailable, result.Status);
        Assert.Equal(before, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());
    }

    [Fact]
    public async Task Read_only_current_head_rejects_malformed_head_evidence_without_rewriting_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        begun.Lease!.Dispose();
        var headPath = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single();
        var malformedHead = new string('g', GovernedLoopExecutionLimits.Sha256HexCharacters);
        await File.WriteAllTextAsync(headPath, malformedHead);
        var before = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();

        var result = await ((IGovernedLoopEffectAttemptReadStore)store).ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, result.Status);
        Assert.Equal(malformedHead, await File.ReadAllTextAsync(headPath));
        Assert.Equal(before, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());
    }

    [Fact]
    public async Task Read_only_current_head_rejects_retained_versions_above_its_configured_bound()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var prepared = Prepare();
        var writer = new GovernedLoopEffectAttemptStore(paths);
        var begun = await writer.BeginAsync(prepared);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await writer.CompareExchangeAsync(prepared.ContentHash, authorized, begun.Lease!)).Status);
        begun.Lease!.Dispose();
        var before = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray();
        var constrained = (IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths, new GovernedLoopEffectAttemptStoreOptions { MaxVersionsPerAttempt = 1 });

        var result = await constrained.ReadAsync(workspaceId, prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Corrupt, result.Status);
        Assert.Equal(before, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath).Order().ToArray());
    }

    [Fact]
    public async Task Headless_recovery_reserves_head_bytes_before_republishing_the_unique_tip()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var encoded = GovernedLoopEffectAttemptRecordCodec.Encode(prepared);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await InjectVersionAsync(paths, prepared);
        var store = new GovernedLoopEffectAttemptStore(
            paths,
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxRecordUtf8Bytes = encoded.Length,
                MaxStoreUtf8Bytes = encoded.Length,
            });

        var result = await store.BeginAsync(prepared);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, result.Status);
        Assert.Empty(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json"));
    }

    [Fact]
    public async Task Headless_resume_reserves_head_bytes_and_rejects_malformed_stable_operation_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var encoded = GovernedLoopEffectAttemptRecordCodec.Encode(prepared);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await InjectVersionAsync(paths, prepared);
        var store = new GovernedLoopEffectAttemptStore(
            paths,
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxRecordUtf8Bytes = encoded.Length,
                MaxStoreUtf8Bytes = encoded.Length,
            });

        var result = await store.ResumeAsync(prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, result.Status);
        Assert.Empty(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
        Assert.Equal(
            GovernedLoopEffectAttemptStoreStatus.Corrupt,
            (await store.ResumeAsync("invalid/stable-operation", prepared.Payload.EffectGeneration)).Status);
    }

    [Fact]
    public async Task Missing_tampered_or_noncanonical_chain_evidence_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.ContentHash, authorized, begun.Lease!)).Status);
        begun.Lease!.Dispose();

        var initialPath = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json")
            .Single(path => Path.GetFileName(path).Contains(prepared.ContentHash, StringComparison.Ordinal));
        File.Delete(initialPath);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared)).Status);

        await File.WriteAllBytesAsync(initialPath, GovernedLoopEffectAttemptRecordCodec.Encode(prepared));
        var headPath = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single();
        await File.WriteAllTextAsync(headPath, Hash('f'));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared)).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Lagging_head_recovers_unique_authority_boundary_or_outcome_successor(int successorCount)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        var versions = Successors(prepared);
        var current = prepared;
        for (var index = 0; index < successorCount; index++)
        {
            Assert.Equal(
                GovernedLoopEffectAttemptStoreStatus.Created,
                (await store.CompareExchangeAsync(current.ContentHash, versions[index], begun.Lease!)).Status);
            current = versions[index];
        }
        begun.Lease!.Dispose();

        var headPath = Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head").Single();
        await File.WriteAllTextAsync(headPath, current.PreviousContentHash!);

        var recovered = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, recovered.Status);
        Assert.Equal(current.ContentHash, recovered.Attempt!.ContentHash);
        Assert.Equal(current.ContentHash, await File.ReadAllTextAsync(headPath));
        recovered.Lease?.Dispose();
    }

    [Fact]
    public async Task Valid_fork_and_disconnected_second_root_are_detected_as_corruption()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        var authorityA = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(
            GovernedLoopEffectAttemptStoreStatus.Created,
            (await store.CompareExchangeAsync(prepared.ContentHash, authorityA, begun.Lease!)).Status);
        begun.Lease!.Dispose();
        var authorityB = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('9'), _now.AddSeconds(1));
        await InjectVersionAsync(paths, authorityB);

        Assert.Equal(
            GovernedLoopEffectAttemptStoreStatus.Corrupt,
            (await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared)).Status);

        File.Delete(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json")
            .Single(path => Path.GetFileName(path).Contains(authorityB.ContentHash, StringComparison.Ordinal)));
        var secondRoot = Prepare(inputFingerprint: Hash('9'));
        await InjectVersionAsync(paths, secondRoot);
        Assert.Equal(
            GovernedLoopEffectAttemptStoreStatus.Corrupt,
            (await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared)).Status);
    }

    [Fact]
    public async Task Count_version_byte_and_option_bounds_apply_without_erasing_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new GovernedLoopEffectAttemptStoreOptions
        {
            MaxAttempts = 1,
            MaxRecordUtf8Bytes = GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes,
            MaxStoreUtf8Bytes = GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes,
            MaxVersionsPerAttempt = 2,
        };
        var store = new GovernedLoopEffectAttemptStore(paths, options);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, begun.Status);

        var secondIdentity = Prepare(effectId: "effect-2", idempotencyOperationId: "effect-operation-2");
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await store.BeginAsync(secondIdentity)).Status);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.ContentHash, authorized, begun.Lease!)).Status);
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, _now.AddSeconds(2));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await store.CompareExchangeAsync(authorized.ContentHash, crossed, begun.Lease!)).Status);
        Assert.Equal(2, Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json").Count());
        begun.Lease!.Dispose();

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAttemptStore(paths, options with { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAttemptStore(paths, options with { MaxVersionsPerAttempt = 17 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAttemptStore(paths, options with { MaxRecordUtf8Bytes = GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes + 1 }));

        using var byteWorkspace = new TestWorkspace();
        var bytePaths = new WorkspacePaths(byteWorkspace.RootPath);
        var encodedLength = GovernedLoopEffectAttemptRecordCodec.Encode(prepared).Length;
        var byteBound = encodedLength + GovernedLoopExecutionLimits.Sha256HexCharacters;
        var byteStore = new GovernedLoopEffectAttemptStore(
            bytePaths,
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxAttempts = 2,
                MaxRecordUtf8Bytes = byteBound,
                MaxStoreUtf8Bytes = byteBound,
                MaxVersionsPerAttempt = 2,
            });
        var byteCreated = await byteStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, byteCreated.Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await byteStore.BeginAsync(secondIdentity)).Status);
        byteCreated.Lease!.Dispose();

        using var recordWorkspace = new TestWorkspace();
        var recordStore = new GovernedLoopEffectAttemptStore(
            new WorkspacePaths(recordWorkspace.RootPath),
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxRecordUtf8Bytes = encodedLength - 1,
                MaxStoreUtf8Bytes = GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes,
            });
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await recordStore.BeginAsync(prepared)).Status);
    }

    [Fact]
    public async Task Concurrent_begin_has_one_owner_and_one_durable_intent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var stores = Enumerable.Range(0, 16).Select(_ => new GovernedLoopEffectAttemptStore(paths)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => store.BeginAsync(prepared)));

        Assert.Single(results, result => result.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed && result.Lease is not null);
        Assert.All(
            results.Where(result => result.Lease is null),
            result => Assert.Equal(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, result.Status));
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json"));
        foreach (var result in results)
        {
            result.Lease?.Dispose();
        }
    }

    [Fact]
    public async Task Exact_pre_intent_owner_orphan_does_not_consume_a_second_attempt_quota_slot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        var material = Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-attempt-storage.v1\n{prepared.Payload.OperationId}\n{prepared.Payload.EffectGeneration}");
        var storageKey = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, storageKey + ".owner"), []);
        var store = new GovernedLoopEffectAttemptStore(
            paths,
            new GovernedLoopEffectAttemptStoreOptions { MaxAttempts = 1 });

        var other = Prepare(effectId: "effect-2", idempotencyOperationId: "effect-operation-2");
        var resumed = await store.BeginAsync(other);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, resumed.Status);
        Assert.NotNull(resumed.Lease);
        resumed.Lease!.Dispose();
        var third = Prepare(effectId: "effect-3", idempotencyOperationId: "effect-operation-3");
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await store.BeginAsync(third)).Status);
    }

    [Fact]
    public async Task Invalid_options_and_attempt_inputs_fail_closed_without_publishing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var encodedLength = GovernedLoopEffectAttemptRecordCodec.Encode(prepared).Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAttemptStore(
            paths,
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxRecordUtf8Bytes = encodedLength,
                MaxStoreUtf8Bytes = encodedLength - 1,
            }));

        var store = new GovernedLoopEffectAttemptStore(paths);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await store.BeginAsync(null!)).Status);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await store.BeginAsync(authorized)).Status);
    }

    [Fact]
    public async Task Preparation_claim_expiry_prevents_intent_publication()
    {
        using var workspace = new TestWorkspace();
        var prepared = Prepare();
        var result = await new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspace.RootPath))
            .BeginWithPreparationClaimAsync(prepared, _ => Task.FromResult(false));

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.PreparationExpired, result.Status);
        var attemptsPath = new WorkspacePaths(workspace.RootPath).GovernedLoopEffectAttemptsPath;
        Assert.Empty(Directory.EnumerateFiles(attemptsPath, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(attemptsPath, "*.head"));
        Assert.Empty(Directory.EnumerateFiles(attemptsPath, "*.owner"));
    }

    [Fact]
    public async Task Resume_replays_a_released_owner_and_reports_an_active_owner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var first = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);
        using var lease = first.Lease!;

        var active = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync(prepared.Payload.OperationId, prepared.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, active.Status);
        Assert.Null(active.Lease);

        lease.Dispose();
        var replay = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync(prepared.Payload.OperationId, prepared.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, replay.Status);
        Assert.NotNull(replay.Lease);
        replay.Lease!.Dispose();
    }

    [Fact]
    public async Task Compare_exchange_fails_closed_for_head_pressure_missing_current_and_illegal_successor()
    {
        using (var pressureWorkspace = new TestWorkspace())
        {
            var pressurePaths = new WorkspacePaths(pressureWorkspace.RootPath);
            var pressurePrepared = Prepare();
            var encodedLength = GovernedLoopEffectAttemptRecordCodec.Encode(pressurePrepared).Length;
            var pressureStore = new GovernedLoopEffectAttemptStore(
                pressurePaths,
                new GovernedLoopEffectAttemptStoreOptions
                {
                    MaxRecordUtf8Bytes = encodedLength,
                    MaxStoreUtf8Bytes = encodedLength + 64,
                });
            var begun = await pressureStore.BeginAsync(pressurePrepared);
            File.WriteAllText(Path.Combine(pressurePaths.GovernedLoopEffectAttemptsPath, new string('a', 64) + ".head"), new string('b', 64));
            File.Delete(Path.Combine(pressurePaths.GovernedLoopEffectAttemptsPath, StorageKeyFor(pressurePrepared) + ".head"));

            var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(pressurePrepared, Hash('8'), _now.AddSeconds(1));
            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await pressureStore.CompareExchangeAsync(pressurePrepared.ContentHash, authorized, begun.Lease!)).Status);
            begun.Lease!.Dispose();
        }

        using (var missingWorkspace = new TestWorkspace())
        {
            var missingPaths = new WorkspacePaths(missingWorkspace.RootPath);
            var missingPrepared = Prepare();
            var missingStore = new GovernedLoopEffectAttemptStore(missingPaths);
            var begun = await missingStore.BeginAsync(missingPrepared);
            foreach (var path in Directory.EnumerateFiles(missingPaths.GovernedLoopEffectAttemptsPath, "*.json"))
            {
                File.Delete(path);
            }

            var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(missingPrepared, Hash('8'), _now.AddSeconds(1));
            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Conflict, (await missingStore.CompareExchangeAsync(missingPrepared.ContentHash, authorized, begun.Lease!)).Status);
            begun.Lease!.Dispose();
        }

        using var illegalWorkspace = new TestWorkspace();
        var illegalPaths = new WorkspacePaths(illegalWorkspace.RootPath);
        var illegalPrepared = Prepare();
        var illegalStore = new GovernedLoopEffectAttemptStore(illegalPaths);
        var illegalBegun = await illegalStore.BeginAsync(illegalPrepared);
        var authorizedSuccessor = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(illegalPrepared, Hash('8'), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(
            authorizedSuccessor,
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            _now.AddSeconds(2));

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Conflict, (await illegalStore.CompareExchangeAsync(illegalPrepared.ContentHash, crossed, illegalBegun.Lease!)).Status);
        illegalBegun.Lease!.Dispose();
    }

    [Fact]
    public async Task Initial_owner_file_contention_reports_operation_in_progress()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        var ownerPath = Path.Combine(paths.GovernedLoopEffectAttemptsPath, StorageKeyFor(prepared) + ".owner");
        using var owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);

        var result = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, result.Status);
        Assert.Null(result.Lease);
    }

    [Theory]
    [InlineData(".bad.tmp")]
    [InlineData("not-a-version.json")]
    [InlineData("not-a-head.head")]
    [InlineData("not-an-owner.owner")]
    public async Task Unsupported_effect_attempt_artifact_names_fail_closed(string fileName)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, fileName), "unsupported");

        var result = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync("missing-operation", 1);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Value_bearing_owner_and_child_directory_are_corrupt()
    {
        using (var ownerWorkspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(ownerWorkspace.RootPath);
            var prepared = Prepare();
            Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
            await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, StorageKeyFor(prepared) + ".owner"), "unexpected-value");

            var result = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync(prepared.Payload.OperationId, prepared.Payload.EffectGeneration);

            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, result.Status);
        }

        using var directoryWorkspace = new TestWorkspace();
        var directoryPaths = new WorkspacePaths(directoryWorkspace.RootPath);
        Directory.CreateDirectory(Path.Combine(directoryPaths.GovernedLoopEffectAttemptsPath, "nested"));
        var directoryResult = await new GovernedLoopEffectAttemptStore(directoryPaths).ResumeAsync("missing-operation", 1);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, directoryResult.Status);
    }

    [Fact]
    public async Task Too_many_versions_are_rejected_before_recovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        var versions = Successors(prepared);
        var current = prepared;
        foreach (var version in versions)
        {
            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await store.CompareExchangeAsync(current.ContentHash, version, begun.Lease!)).Status);
            current = version;
        }
        begun.Lease!.Dispose();

        var limited = new GovernedLoopEffectAttemptStore(paths, new GovernedLoopEffectAttemptStoreOptions { MaxVersionsPerAttempt = 2 });
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, (await limited.BeginAsync(prepared)).Status);
    }

    [Fact]
    public async Task Wrong_identity_version_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var other = Prepare(effectId: "effect-other", idempotencyOperationId: "effect-operation-other");
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        await File.WriteAllBytesAsync(
            Path.Combine(paths.GovernedLoopEffectAttemptsPath, $"{StorageKeyFor(prepared)}.{other.ContentHash}.json"),
            GovernedLoopEffectAttemptRecordCodec.Encode(other));

        var result = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(prepared);

        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Resume_republishes_a_missing_head_and_reports_not_found_for_unknown_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = Prepare();
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared);
        begun.Lease!.Dispose();
        File.Delete(Path.Combine(paths.GovernedLoopEffectAttemptsPath, StorageKeyFor(prepared) + ".head"));

        var resumed = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync(prepared.Payload.OperationId, prepared.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, resumed.Status);
        resumed.Lease!.Dispose();
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.NotFound, (await store.ResumeAsync("missing-operation", 1)).Status);
    }

    [Fact]
    public async Task Begin_replays_or_backpressures_a_current_intent_when_its_head_is_missing()
    {
        var prepared = Prepare();
        var encodedLength = GovernedLoopEffectAttemptRecordCodec.Encode(prepared).Length;
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var store = new GovernedLoopEffectAttemptStore(paths);
            var begun = await store.BeginAsync(prepared);
            begun.Lease!.Dispose();
            File.Delete(Path.Combine(paths.GovernedLoopEffectAttemptsPath, StorageKeyFor(prepared) + ".head"));

            var replay = await store.BeginAsync(prepared);
            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, replay.Status);
            replay.Lease!.Dispose();
        }

        using var pressureWorkspace = new TestWorkspace();
        var pressurePaths = new WorkspacePaths(pressureWorkspace.RootPath);
        var pressureStore = new GovernedLoopEffectAttemptStore(
            pressurePaths,
            new GovernedLoopEffectAttemptStoreOptions
            {
                MaxRecordUtf8Bytes = encodedLength,
                MaxStoreUtf8Bytes = encodedLength + 64,
            });
        var pressureBegun = await pressureStore.BeginAsync(prepared);
        pressureBegun.Lease!.Dispose();
        File.WriteAllText(Path.Combine(pressurePaths.GovernedLoopEffectAttemptsPath, new string('a', 64) + ".head"), new string('b', 64));
        File.Delete(Path.Combine(pressurePaths.GovernedLoopEffectAttemptsPath, StorageKeyFor(prepared) + ".head"));
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Backpressured, (await pressureStore.BeginAsync(prepared)).Status);
    }

    [Fact]
    public async Task Mutation_lock_retry_exhaustion_and_unavailable_roots_fail_closed()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
            await using var externalLock = new FileStream(
                Path.Combine(paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var result = await new GovernedLoopEffectAttemptStore(paths).BeginAsync(Prepare());
            Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Unavailable, result.Status);
        }

        using var unavailableWorkspace = new TestWorkspace();
        var unavailablePaths = new WorkspacePaths(unavailableWorkspace.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(unavailablePaths.GovernedLoopEffectAttemptsPath)!);
        await File.WriteAllTextAsync(unavailablePaths.GovernedLoopEffectAttemptsPath, "not-a-directory");
        Assert.Equal(
            GovernedLoopEffectAttemptStoreStatus.Unavailable,
            (await new GovernedLoopEffectAttemptStore(unavailablePaths).BeginAsync(Prepare())).Status);
    }

    [Fact]
    public async Task Mutation_lock_retry_honors_caller_cancellation_and_later_acquires_after_release()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        Directory.CreateDirectory(paths.GovernedLoopEffectAttemptsPath);
        using var cancellation = new CancellationTokenSource();

        await using (var externalLock = new FileStream(
            Path.Combine(paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var pending = store.BeginAsync(Prepare(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        var acquired = await store.BeginAsync(Prepare());
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, acquired.Status);
        acquired.Lease!.Dispose();
    }

    [Fact]
    public async Task Compare_exchange_fails_closed_while_mutation_lock_is_held()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopEffectAttemptStore(paths);
        var prepared = Prepare();
        var begun = await store.BeginAsync(prepared);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), _now.AddSeconds(1));
        await using var externalLock = new FileStream(
            Path.Combine(paths.GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await store.CompareExchangeAsync(
            prepared.ContentHash,
            authorized,
            begun.Lease!,
            cancellation.Token);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Unavailable, result.Status);
        begun.Lease!.Dispose();
    }

    private static GovernedLoopEffectAttempt Prepare(
        string? inputFingerprint = null,
        string effectId = "effect-1",
        string idempotencyOperationId = "effect-operation-1")
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/effects/probe", out var capabilityId, out var capabilityError), capabilityError?.Message);
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out var versionError), versionError?.Message);
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('1'), out var descriptorHash, out var hashError), hashError?.Message);
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out var providerError), providerError?.Message);
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('a'));
        var binding = GovernedLoopExecutionBinding.Create(1, "run-1", revision, 1);
        return GovernedLoopEffectAttemptContract.Prepare(
            binding,
            "action-1",
            1,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, "effects/probe"),
            "probe/observe",
            Hash('b'),
            effectId,
            idempotencyOperationId,
            1,
            inputFingerprint ?? Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            "probe-before",
            _now);
    }

    private static IReadOnlyList<GovernedLoopEffectAttempt> Successors(GovernedLoopEffectAttempt prepared)
    {
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
            "probe-outcome",
            "probe-after",
            _now.AddSeconds(3));
        return [authorized, crossed, observed];
    }

    private static Task InjectVersionAsync(WorkspacePaths paths, GovernedLoopEffectAttempt attempt)
    {
        var material = Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-attempt-storage.v1\n{attempt.Payload.OperationId}\n{attempt.Payload.EffectGeneration}");
        var storageKey = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        var path = Path.Combine(paths.GovernedLoopEffectAttemptsPath, $"{storageKey}.{attempt.ContentHash}.json");
        return File.WriteAllBytesAsync(path, GovernedLoopEffectAttemptRecordCodec.Encode(attempt));
    }

    private static string StorageKeyFor(GovernedLoopEffectAttempt attempt)
    {
        var material = Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-attempt-storage.v1\n{attempt.Payload.OperationId}\n{attempt.Payload.EffectGeneration}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static string Hash(char value) => new(value, 64);
}
