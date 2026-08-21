using System.Security.Cryptography;
using System.Text;
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

    private static string Hash(char value) => new(value, 64);
}
