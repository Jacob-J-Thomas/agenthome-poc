using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Secrets.Redaction.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Credentials.Leases;
using EmbodySense.Core.Persistence.Credentials.Leases.Models;
using EmbodySense.Core.Persistence.Credentials.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class CredentialLeaseAttemptStoreTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_begin_uses_hashed_filenames_and_cross_instance_owner_exclusion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var created = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, created.Status);
        Assert.NotNull(created.Lease);
        Assert.Equal(prepared, created.History!.Current);
        var artifacts = Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath)
            .Select(Path.GetFileName)
            .Where(file => file != ".custom-loop-mutations.lock")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, artifacts.Length);
        Assert.All(artifacts, file => Assert.Matches("^[0-9a-f]{64}(\\.[0-9a-f]{64}\\.json|\\.head|\\.owner)$", file!));
        var persisted = await File.ReadAllTextAsync(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Single());
        Assert.DoesNotContain("raw-private-target", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialValue", persisted, StringComparison.OrdinalIgnoreCase);

        var concurrent = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.OperationInProgress, concurrent.Status);
        created.Lease!.Dispose();

        var restarted = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, restarted.Status);
        Assert.NotNull(restarted.Lease);
        restarted.Lease!.Dispose();
    }

    [Fact]
    public async Task Direct_successors_are_append_only_restart_safe_and_stale_heads_conflict()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CredentialLeaseAttemptStore(paths);
        var intent = Intent();
        var preparedHistory = Prepared(intent);
        var begun = await store.BeginAsync(intent, preparedHistory.Current);
        var owner = Assert.IsAssignableFrom<ICredentialLeaseAttemptLease>(begun.Lease);
        var authorized = Append(preparedHistory, CredentialLeasePhase.Authorized, _now.AddSeconds(1), Hash('a'), RegistryEvidence(intent));
        var boundary = Append(authorized, CredentialLeasePhase.RedemptionBoundaryReached, _now.AddSeconds(2));
        var redeemed = Append(boundary, CredentialLeasePhase.Redeemed, _now.AddSeconds(3));

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(preparedHistory.Current.ContentHash, authorized, owner)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.Current.ContentHash, boundary, owner)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(boundary.Current.ContentHash, redeemed, owner)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Conflict, (await store.CompareExchangeAsync(preparedHistory.Current.ContentHash, authorized, owner)).Status);
        Assert.Equal(4, Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Count());
        owner.Dispose();

        var replay = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, preparedHistory.Current);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, replay.Status);
        Assert.Equal(CredentialLeasePhase.Redeemed, replay.History!.Current.Phase);
        Assert.Null(replay.Lease);
    }

    [Fact]
    public async Task Missing_head_is_recovered_but_malformed_or_tampered_history_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var created = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);
        created.Lease!.Dispose();
        File.Delete(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.head").Single());

        var recovered = await new CredentialLeaseAttemptStore(paths).ResumeAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, recovered.Status);
        Assert.Single(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.head"));
        recovered.Lease!.Dispose();

        var recordPath = Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Single();
        await File.AppendAllTextAsync(recordPath, " ");
        var corrupt = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Corrupt, corrupt.Status);
        Assert.Null(corrupt.Lease);
    }

    [Fact]
    public async Task Attempt_and_byte_quotas_backpressure_without_erasing_retained_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new CredentialLeaseAttemptStoreOptions { MaxAttempts = 1, MaxStoreUtf8Bytes = 300_000 };
        var store = new CredentialLeaseAttemptStore(paths, options);
        var first = Intent();
        var firstResult = await store.BeginAsync(first, CredentialLeaseContract.Prepare(first, _now));
        firstResult.Lease!.Dispose();
        var second = Rehash(first with { LeaseId = "lease-2", CredentialUseOperationId = "credential-use-2" });

        var backpressured = await store.BeginAsync(second, CredentialLeaseContract.Prepare(second, _now));

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Backpressured, backpressured.Status);
        Assert.Single(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json"));
        var retained = await new CredentialLeaseAttemptStore(paths, options).BeginAsync(first, CredentialLeaseContract.Prepare(first, _now));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, retained.Status);
        retained.Lease!.Dispose();
    }

    [Fact]
    public async Task Byte_quota_reserves_every_successor_before_admission_and_releases_only_after_terminal_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new CredentialLeaseAttemptStoreOptions { MaxAttempts = 2, MaxStoreUtf8Bytes = 300_000, MaxVersionsPerAttempt = 4 };
        var store = new CredentialLeaseAttemptStore(paths, options);
        var first = Intent();
        var firstPrepared = Prepared(first);
        var firstBegun = await store.BeginAsync(first, firstPrepared.Current);
        var second = Rehash(first with { LeaseId = "lease-2", CredentialUseOperationId = "credential-use-2" });

        var rejectedWhileReserved = await store.BeginAsync(second, CredentialLeaseContract.Prepare(second, _now));
        var authorized = Append(firstPrepared, CredentialLeasePhase.Authorized, _now.AddTicks(1), Hash('a'), RegistryEvidence(first));
        var boundary = Append(authorized, CredentialLeasePhase.RedemptionBoundaryReached, _now.AddTicks(2));
        var terminalVersion = CredentialLeaseContract.Advance(first, boundary.Current, CredentialLeasePhase.RedemptionAmbiguous, _now.AddTicks(3), failureCode: CredentialFailureCode.OutcomeUncertain);
        var terminal = CredentialLeaseContract.CreateHistory(first, [.. boundary.Versions, terminalVersion]);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Backpressured, rejectedWhileReserved.Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(firstPrepared.Current.ContentHash, authorized, firstBegun.Lease!)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.Current.ContentHash, boundary, firstBegun.Lease!)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(boundary.Current.ContentHash, terminal, firstBegun.Lease!)).Status);
        firstBegun.Lease!.Dispose();

        var admittedAfterTerminal = await store.BeginAsync(second, CredentialLeaseContract.Prepare(second, _now));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, admittedAfterTerminal.Status);
        admittedAfterTerminal.Lease!.Dispose();
    }

    [Fact]
    public async Task Version_quota_requires_capacity_for_every_terminal_protocol_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialLeaseAttemptStore(paths, new CredentialLeaseAttemptStoreOptions { MaxVersionsPerAttempt = 3 }));
        var store = new CredentialLeaseAttemptStore(paths, new CredentialLeaseAttemptStoreOptions { MaxVersionsPerAttempt = 4 });
        var intent = Intent();
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now.AddTicks(1), Hash('a'), RegistryEvidence(intent));
        var boundary = Append(authorized, CredentialLeasePhase.RedemptionBoundaryReached, _now.AddTicks(2));
        var terminalVersion = CredentialLeaseContract.Advance(intent, boundary.Current, CredentialLeasePhase.RedemptionAmbiguous, _now.AddTicks(3), failureCode: CredentialFailureCode.OutcomeUncertain);
        var terminal = CredentialLeaseContract.CreateHistory(intent, [.. boundary.Versions, terminalVersion]);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.Current.ContentHash, boundary, begun.Lease!)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(boundary.Current.ContentHash, terminal, begun.Lease!)).Status);
        Assert.Equal(4, Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Count());
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Owner_only_crash_artifacts_count_toward_the_attempt_quota()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CredentialLeaseAttemptsPath);
        await File.WriteAllBytesAsync(Path.Combine(paths.CredentialLeaseAttemptsPath, new string('a', 64) + ".owner"), []);
        var store = new CredentialLeaseAttemptStore(paths, new CredentialLeaseAttemptStoreOptions { MaxAttempts = 1 });
        var intent = Intent();

        var result = await store.BeginAsync(intent, CredentialLeaseContract.Prepare(intent, _now));

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Backpressured, result.Status);
        Assert.Empty(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json"));
    }

    [Fact]
    public async Task Owner_only_crash_artifact_reuses_its_reserved_capacity_for_the_same_attempt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var encodedLength = CredentialLeaseAttemptRecordCodec.Encode(Prepared(intent)).Length;
        var options = new CredentialLeaseAttemptStoreOptions
        {
            MaxAttempts = 1,
            MaxRecordUtf8Bytes = encodedLength,
            MaxStoreUtf8Bytes = checked((encodedLength * 4) + prepared.ContentHash.Length),
            MaxVersionsPerAttempt = 4,
        };
        var initiallyBegun = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);
        initiallyBegun.Lease!.Dispose();
        File.Delete(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Single());
        File.Delete(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.head").Single());

        var resumed = await new CredentialLeaseAttemptStore(paths, options).BeginAsync(intent, prepared);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, resumed.Status);
        Assert.NotNull(resumed.Lease);
        Assert.Single(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json"));
        resumed.Lease!.Dispose();
    }

    [Fact]
    public async Task Read_observes_the_exact_current_posture_without_taking_ownership()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CredentialLeaseAttemptStore(paths);
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var begun = await store.BeginAsync(intent, prepared);

        var observed = await new CredentialLeaseAttemptStore(paths).ReadAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);
        var concurrent = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, prepared);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, observed.Status);
        Assert.Equal(begun.History!.Intent.ContentHash, observed.History!.Intent.ContentHash);
        Assert.Equal(begun.History.Versions, observed.History.Versions);
        Assert.Null(observed.Lease);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.OperationInProgress, concurrent.Status);
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Lagging_head_is_repaired_to_the_latest_immutable_history_without_erasing_versions()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CredentialLeaseAttemptStore(paths);
        var intent = Intent();
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now.AddTicks(1), Hash('a'), RegistryEvidence(intent));
        var boundary = Append(authorized, CredentialLeasePhase.RedemptionBoundaryReached, _now.AddTicks(2));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(authorized.Current.ContentHash, boundary, begun.Lease!)).Status);
        begun.Lease!.Dispose();
        var headPath = Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.head").Single();
        await File.WriteAllTextAsync(headPath, authorized.Current.ContentHash);

        var recovered = await new CredentialLeaseAttemptStore(paths).ResumeAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, recovered.Status);
        Assert.Equal(boundary.Current.ContentHash, recovered.History!.Current.ContentHash);
        Assert.Equal(boundary.Current.ContentHash, await File.ReadAllTextAsync(headPath));
        Assert.Equal(3, Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").Count());
        recovered.Lease!.Dispose();
    }

    [Fact]
    public async Task Forked_histories_fail_closed_and_preserve_every_conflicting_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CredentialLeaseAttemptStore(paths);
        var intent = Intent();
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        begun.Lease!.Dispose();
        var first = Append(prepared, CredentialLeasePhase.Authorized, _now.AddTicks(1), Hash('a'), RegistryEvidence(intent));
        var fork = Append(prepared, CredentialLeasePhase.Authorized, _now.AddTicks(2), Hash('b'), RegistryEvidence(intent));
        await WriteHistoryArtifactAsync(paths, first);
        await WriteHistoryArtifactAsync(paths, fork);
        var artifactCount = Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath).Count();

        var corrupt = await new CredentialLeaseAttemptStore(paths).ResumeAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Corrupt, corrupt.Status);
        Assert.Equal(artifactCount, Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath).Count());
    }

    [Fact]
    public async Task Missing_predecessor_and_interrupted_atomic_publication_fail_closed_without_cleanup()
    {
        using var missingWorkspace = new TestWorkspace();
        var missingPaths = new WorkspacePaths(missingWorkspace.RootPath);
        var intent = Intent();
        var prepared = Prepared(intent);
        var begun = await new CredentialLeaseAttemptStore(missingPaths).BeginAsync(intent, prepared.Current);
        begun.Lease!.Dispose();
        File.Delete(Directory.EnumerateFiles(missingPaths.CredentialLeaseAttemptsPath, "*.json").Single());
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now.AddTicks(1), Hash('a'), RegistryEvidence(intent));
        var boundary = Append(authorized, CredentialLeasePhase.RedemptionBoundaryReached, _now.AddTicks(2));
        var orphan = await WriteHistoryArtifactAsync(missingPaths, boundary);

        var missing = await new CredentialLeaseAttemptStore(missingPaths).ResumeAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Corrupt, missing.Status);
        Assert.True(File.Exists(orphan));

        using var interruptedWorkspace = new TestWorkspace();
        var interruptedPaths = new WorkspacePaths(interruptedWorkspace.RootPath);
        var interrupted = await new CredentialLeaseAttemptStore(interruptedPaths).BeginAsync(intent, prepared.Current);
        interrupted.Lease!.Dispose();
        var headName = Path.GetFileName(Directory.EnumerateFiles(interruptedPaths.CredentialLeaseAttemptsPath, "*.head").Single());
        var temporaryPath = Path.Combine(interruptedPaths.CredentialLeaseAttemptsPath, $".{headName}.{new string('a', 32)}.tmp");
        await File.WriteAllTextAsync(temporaryPath, prepared.Current.ContentHash);

        var torn = await new CredentialLeaseAttemptStore(interruptedPaths).ResumeAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Corrupt, torn.Status);
        Assert.True(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Identical_operation_identity_is_isolated_by_workspace_root()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _now);

        var first = await new CredentialLeaseAttemptStore(new WorkspacePaths(firstWorkspace.RootPath)).BeginAsync(intent, prepared);
        var second = await new CredentialLeaseAttemptStore(new WorkspacePaths(secondWorkspace.RootPath)).BeginAsync(intent, prepared);

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, first.Status);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, second.Status);
        Assert.NotEqual(firstWorkspace.RootPath, secondWorkspace.RootPath);
        first.Lease!.Dispose();
        second.Lease!.Dispose();
    }

    [Fact]
    public async Task Replaced_lease_directory_reparse_point_fails_closed_without_writing_outside_workspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CredentialRegistryPath);
        Directory.CreateSymbolicLink(paths.CredentialLeaseAttemptsPath, outside.RootPath);
        var intent = Intent();

        var result = await new CredentialLeaseAttemptStore(paths).BeginAsync(intent, CredentialLeaseContract.Prepare(intent, _now));

        Assert.Equal(CredentialLeaseAttemptStoreStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Redemption_gate_rereads_exact_registry_under_reference_ordering_gate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var store = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var owner = begun.Lease!;
        var registry = new StaticRegistry(RegistryRead(intent));
        var match = CredentialLeaseRegistryMatcher.Match(intent, await registry.ReadAsync(), _now);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, match.EvidenceHash);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, owner)).Status);

        var entered = await Gate(registry, store, authorized, new FixedTimeProvider(_now.AddTicks(1))).TryEnterAsync(authorized, owner, _now.AddTicks(1));

        Assert.Equal(CredentialLeaseBoundaryStatus.Entered, entered.Status);
        Assert.Equal(CredentialLeasePhase.RedemptionBoundaryReached, entered.History!.Current.Phase);
        owner.Dispose();

        using var deniedWorkspace = new TestWorkspace();
        var deniedPaths = new WorkspacePaths(deniedWorkspace.RootPath);
        var deniedStore = new CredentialLeaseAttemptStore(deniedPaths);
        var deniedPrepared = Prepared(intent);
        var deniedBegun = await deniedStore.BeginAsync(intent, deniedPrepared.Current);
        var deniedAuthorized = Append(deniedPrepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, match.EvidenceHash);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await deniedStore.CompareExchangeAsync(deniedPrepared.Current.ContentHash, deniedAuthorized, deniedBegun.Lease!)).Status);
        var drifted = new StaticRegistry(RegistryRead(intent) with { RegistryRevision = 8 });

        var denied = await Gate(drifted, deniedStore, deniedAuthorized, new FixedTimeProvider(_now.AddTicks(1))).TryEnterAsync(deniedAuthorized, deniedBegun.Lease!, _now.AddTicks(1));

        Assert.Equal(CredentialLeaseBoundaryStatus.NotRedeemed, denied.Status);
        Assert.Equal(CredentialLeasePhase.NotRedeemed, denied.History!.Current.Phase);
        deniedBegun.Lease!.Dispose();
    }

    [Fact]
    public async Task Redemption_gate_resamples_trusted_time_after_registry_io_and_rejects_stale_preexpiry_caller_time()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var registry = new StaticRegistry(RegistryRead(intent));
        var store = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, RegistryEvidence(intent));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        var gate = Gate(registry, store, authorized, new FixedTimeProvider(intent.EffectiveExpiresAtUtc));

        var denied = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(1));

        Assert.Equal(CredentialLeaseBoundaryStatus.NotRedeemed, denied.Status);
        Assert.Equal(CredentialLeasePhase.NotRedeemed, denied.History!.Current.Phase);
        Assert.Equal(CredentialFailureCode.Expired, denied.History.Current.FailureCode);
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Redemption_gate_revalidates_current_authority_under_the_retained_transaction()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var registry = new StaticRegistry(RegistryRead(intent));
        var store = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, RegistryEvidence(intent));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        var gate = Gate(registry, store, authorized, new FixedTimeProvider(_now.AddTicks(1)), CredentialLeaseCurrentVerificationStatus.Denied);

        var denied = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(1));

        Assert.Equal(CredentialLeaseBoundaryStatus.NotRedeemed, denied.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, denied.History!.Current.FailureCode);
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Redemption_gate_acquires_authority_before_the_reference_mutex()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var intent = Intent();
        var registry = new StaticRegistry(RegistryRead(intent));
        var store = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await store.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, RegistryEvidence(intent));
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await store.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        var authority = new ReferenceMutexOrderingProbeCapabilityAuthorityTransaction(intent.Execution.WorkspaceId, Reference());
        var gate = new CredentialLeaseRedemptionGate(
            registry,
            store,
            new CredentialLeaseCurrentAuthorityVerifier(new StaticCurrentAuthoritySource(authorized.Intent, authorized.Current.CurrentAuthorityEvidenceHash!, CredentialLeaseCurrentVerificationStatus.Authorized)),
            authority,
            new FixedTimeProvider(_now.AddTicks(1)));

        var entered = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(1));

        Assert.False(authority.ReferenceMutexWasHeldBeforeAuthority);
        Assert.Equal(CredentialLeaseBoundaryStatus.Entered, entered.Status);
        begun.Lease!.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restrictive_lifecycle_mutation_and_boundary_publication_preserve_both_legal_orderings(bool revokeFirst)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var intent = seeded.Intent;
        var registry = seeded.Registry;
        var read = await registry.ReadAsync();
        var entry = Assert.Single(read.Entries);
        var attempts = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await attempts.BeginAsync(intent, prepared.Current);
        Assert.Equal(intent.Registry.RegistryRevision, read.RegistryRevision);
        Assert.Equal(intent.Registry.BindingHash, entry.BindingHash.Value);
        Assert.Equal(intent.Registry.ConsentReferenceId, entry.ConsentReference.Value);
        Assert.Equal(intent.Registry.ProviderId, entry.Reference.ProviderId.Value);
        Assert.Equal(intent.Capability.CapabilityId, entry.Binding.Capability.Id.Value);
        Assert.Equal(intent.Capability.CapabilityVersion, entry.Binding.Capability.Version.Value);
        Assert.Equal(intent.Capability.CapabilityDescriptorHash, entry.Binding.Capability.Hash.Value);
        Assert.Equal(intent.Capability.CapabilityProviderId, entry.Binding.Implementation.ProviderId.Value);
        Assert.Equal(intent.Capability.CapabilityImplementationId, entry.Binding.Implementation.ImplementationId);
        Assert.Equal(intent.Capability.SecretRequirement, entry.Binding.Requirement.Name);
        Assert.Equal(intent.Execution.WorkspaceId, entry.Binding.Scope.WorkspaceId);
        Assert.Equal(intent.Execution.RoleId, entry.Binding.Scope.RoleId);
        Assert.Equal(intent.Execution.LoopId, entry.Binding.Scope.LoopId);
        Assert.Equal(intent.Execution.DeclaredLoopRevision, entry.Binding.Scope.LoopRevision);
        Assert.Equal(intent.Effect.NodeId, entry.Binding.Scope.NodeId);
        Assert.Equal(intent.Target.TargetClass, entry.Binding.Scope.Service);
        Assert.Equal(intent.Target.OperationClass, entry.Binding.Scope.OperationClass);
        Assert.Equal(intent.Execution.ActorId, entry.Binding.Scope.ActorId);
        Assert.Equal(intent.Target.TargetFingerprint, CredentialLeaseContract.ComputeTargetFingerprint(intent.Target.TargetClass, System.Text.Encoding.UTF8.GetBytes(entry.Binding.Scope.Target!)));
        Assert.True(entry.ConsentGranted);
        Assert.Equal(CredentialLifecycleStatus.Active, entry.Reference.Status);
        Assert.True(entry.Reference.ExpiresAtUtc > _now);
        Assert.Equal(CredentialProviderHealthStatus.Available, entry.Health);
        var match = CredentialLeaseRegistryMatcher.Match(intent, read, _now);
        Assert.True(match.Succeeded);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, match.EvidenceHash);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await attempts.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        var gate = Gate(registry, attempts, authorized, new FixedTimeProvider(_now.AddTicks(1)));
        var operationId = Id(revokeFirst ? "revoke-first" : "revoke-after-boundary");
        var preview = await seeded.Lifecycle.PreviewAsync(new CredentialLifecyclePreviewRequest(
            operationId,
            CredentialLifecycleOperationKind.Revoke,
            entry.Reference.Id,
            intent.Execution.WorkspaceId,
            Environment.UserName,
            read.RegistryRevision!.Value));
        var restrictive = new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Revoke,
            operationId,
            entry.Reference.Id,
            intent.Execution.WorkspaceId,
            Environment.UserName,
            read.RegistryRevision.Value,
            _now.AddTicks(1),
            Preview: preview,
            Confirmed: true);

        if (revokeFirst)
        {
            var revoked = await seeded.Lifecycle.ExecuteAsync(restrictive);
            var denied = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(2));

            Assert.Equal(CredentialLifecycleResultStatus.Applied, revoked.Status);
            Assert.Equal(CredentialLeaseBoundaryStatus.NotRedeemed, denied.Status);
            Assert.Equal(CredentialLeasePhase.NotRedeemed, denied.History!.Current.Phase);
        }
        else
        {
            var entered = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(1));
            var revoked = await seeded.Lifecycle.ExecuteAsync(restrictive);
            var retained = await attempts.ReadAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration);

            Assert.Equal(CredentialLeaseBoundaryStatus.Entered, entered.Status);
            Assert.Equal(CredentialLifecycleResultStatus.Applied, revoked.Status);
            Assert.Equal(CredentialLeasePhase.RedemptionBoundaryReached, retained.History!.Current.Phase);
            Assert.Equal(CredentialLifecycleStatus.Revoked, Assert.Single((await registry.ReadAsync()).Entries).Reference.Status);
        }

        begun.Lease!.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Binding_mutation_and_boundary_publication_preserve_both_legal_orderings(bool bindFirst)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var intent = seeded.Intent;
        var read = await seeded.Registry.ReadAsync();
        var entry = Assert.Single(read.Entries);
        var attempts = new CredentialLeaseAttemptStore(paths);
        var prepared = Prepared(intent);
        var begun = await attempts.BeginAsync(intent, prepared.Current);
        var authorized = Append(prepared, CredentialLeasePhase.Authorized, _now, intent.Authority.CurrentAuthorityDecisionHash, CredentialLeaseRegistryMatcher.Match(intent, read, _now).EvidenceHash);
        Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, (await attempts.CompareExchangeAsync(prepared.Current.ContentHash, authorized, begun.Lease!)).Status);
        var gate = Gate(seeded.Registry, attempts, authorized, new FixedTimeProvider(_now.AddTicks(1)));
        var rebound = entry.Binding with { Scope = entry.Binding.Scope with { LoopRevision = entry.Binding.Scope.LoopRevision + 1 } };
        var request = new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Bind,
            Id(bindFirst ? "bind-first" : "bind-after-boundary"),
            entry.Reference.Id,
            intent.Execution.WorkspaceId,
            Environment.UserName,
            read.RegistryRevision!.Value,
            _now.AddTicks(1),
            Binding: rebound);

        if (bindFirst)
        {
            var reboundResult = await seeded.Lifecycle.ExecuteAsync(request);
            var denied = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(2));

            Assert.Equal(CredentialLifecycleResultStatus.Applied, reboundResult.Status);
            Assert.Equal(CredentialLeaseBoundaryStatus.NotRedeemed, denied.Status);
        }
        else
        {
            var entered = await gate.TryEnterAsync(authorized, begun.Lease!, _now.AddTicks(1));
            var reboundResult = await seeded.Lifecycle.ExecuteAsync(request);

            Assert.Equal(CredentialLeaseBoundaryStatus.Entered, entered.Status);
            Assert.Equal(CredentialLifecycleResultStatus.Applied, reboundResult.Status);
        }
        begun.Lease!.Dispose();
    }

    [Fact]
    public async Task Reserved_terminal_evidence_survives_rebind_and_distinguishes_operation_generations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var first = seeded.Intent;
        var second = Rehash(first with { LeaseId = "lease-generation-2", CredentialUseGeneration = 2 });
        var before = await seeded.Registry.ReadAsync();

        Assert.True((await seeded.Registry.ReserveAsync(first, CancellationToken.None)).Succeeded);
        Assert.True((await seeded.Registry.ReserveAsync(second, CancellationToken.None)).Succeeded);
        Assert.Equal(before.RegistryRevision, (await seeded.Registry.ReadAsync()).RegistryRevision);

        var entry = Assert.Single(before.Entries);
        var rebound = entry.Binding with { Scope = entry.Binding.Scope with { LoopRevision = entry.Binding.Scope.LoopRevision + 1 } };
        var reboundResult = await seeded.Lifecycle.ExecuteAsync(new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Bind,
            Id("rebind-after-evidence-reservation"),
            entry.Reference.Id,
            first.Execution.WorkspaceId,
            Environment.UserName,
            before.RegistryRevision!.Value,
            _now.AddTicks(1),
            Binding: rebound));
        Assert.Equal(CredentialLifecycleResultStatus.Applied, reboundResult.Status);

        var firstTerminal = TerminalHistory(first, _now.AddTicks(2));
        var secondTerminal = TerminalHistory(second, _now.AddTicks(3));
        var firstAppend = await seeded.Registry.AppendAsync(Evidence(firstTerminal, entry.Binding.Scope), CancellationToken.None);
        var secondAppend = await seeded.Registry.AppendAsync(Evidence(secondTerminal, entry.Binding.Scope), CancellationToken.None);
        Assert.True(firstAppend.Succeeded, firstAppend.Failure?.Code.ToString());
        Assert.True(secondAppend.Succeeded, secondAppend.Failure?.Code.ToString());

        var restarted = await new CredentialRegistryStore(paths, TestTrust(paths), new CoordinatedCredentialCreateAdapter(), new FixedTimeProvider(_now.AddTicks(4))).ReadAsync();
        Assert.Equal(2, restarted.Evidence.Count);
        Assert.Equal(2, restarted.Evidence.Select(item => item.EvidenceId).Distinct().Count());
        Assert.Contains("\"evidenceReservations\": []", await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reserved_not_redeemed_evidence_survives_rebind_and_consumes_its_capacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var before = await seeded.Registry.ReadAsync();
        var entry = Assert.Single(before.Entries);

        Assert.True((await seeded.Registry.ReserveAsync(seeded.Intent, CancellationToken.None)).Succeeded);

        var rebound = entry.Binding with { Scope = entry.Binding.Scope with { LoopRevision = entry.Binding.Scope.LoopRevision + 1 } };
        var reboundResult = await seeded.Lifecycle.ExecuteAsync(new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Bind,
            Id("rebind-before-not-redeemed-evidence"),
            entry.Reference.Id,
            seeded.Intent.Execution.WorkspaceId,
            Environment.UserName,
            before.RegistryRevision!.Value,
            _now.AddTicks(1),
            Binding: rebound));
        Assert.Equal(CredentialLifecycleResultStatus.Applied, reboundResult.Status);

        var terminal = NotRedeemedHistory(seeded.Intent, _now.AddTicks(2));
        var appended = await seeded.Registry.AppendAsync(Evidence(terminal, entry.Binding.Scope), CancellationToken.None);

        Assert.True(appended.Succeeded, appended.Failure?.Code.ToString());
        Assert.Single((await seeded.Registry.ReadAsync()).Evidence);
        Assert.Contains("\"evidenceReservations\": []", await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_reservation_consumes_exact_evidence_and_operation_quota_before_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var read = await seeded.Registry.ReadAsync();
        var quota = new CredentialRegistryQuota(2, 10, read.Operations.Count + 1, 1, 1024 * 1024);
        var bounded = new CredentialRegistryStore(paths, TestTrust(paths), new CoordinatedCredentialCreateAdapter(), new FixedTimeProvider(_now), quota: quota);
        var second = Rehash(seeded.Intent with { LeaseId = "lease-quota-2", CredentialUseGeneration = 2 });

        var reserved = await bounded.ReserveAsync(seeded.Intent, CancellationToken.None);
        var rejected = await bounded.ReserveAsync(second, CancellationToken.None);

        Assert.True(reserved.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal(CredentialFailureCode.LimitExceeded, rejected.Failure!.Code);
        Assert.True((await bounded.AppendAsync(Evidence(TerminalHistory(seeded.Intent, _now.AddTicks(1)), Assert.Single(read.Entries).Binding.Scope), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Registry_reservation_rejects_insufficient_terminal_artifact_byte_headroom()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistryAsync(paths);
        var read = await seeded.Registry.ReadAsync();
        var retainedBytes = Math.Max(new FileInfo(paths.CredentialRegistryDocumentPath).Length, new FileInfo(paths.CredentialRegistryPrivateDocumentPath).Length);
        var quota = new CredentialRegistryQuota(2, 10, read.Operations.Count + 1, 1, checked((int)retainedBytes + (64 * 1024)));
        var bounded = new CredentialRegistryStore(paths, TestTrust(paths), new CoordinatedCredentialCreateAdapter(), new FixedTimeProvider(_now), quota: quota);

        Assert.True((await bounded.ReadAsync()).Succeeded);
        var reserved = await bounded.ReserveAsync(seeded.Intent, CancellationToken.None);

        Assert.False(reserved.Succeeded);
        Assert.Equal(CredentialFailureCode.LimitExceeded, reserved.Failure!.Code);
        Assert.Contains("\"evidenceReservations\": []", await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prepared", CredentialLeasePhase.IntentPrepared, CredentialLeasePhase.NotRedeemed)]
    [InlineData("authorized", CredentialLeasePhase.Authorized, CredentialLeasePhase.NotRedeemed)]
    [InlineData("boundary", CredentialLeasePhase.RedemptionBoundaryReached, CredentialLeasePhase.RedemptionAmbiguous)]
    [InlineData("redeemed", CredentialLeasePhase.Redeemed, CredentialLeasePhase.Redeemed)]
    public async Task Abrupt_external_process_loss_preserves_exact_phase_and_never_reopens_consumed_attempt(
        string hostedPhase,
        CredentialLeasePhase expectedInterruptedPhase,
        CredentialLeasePhase expectedTerminalPhase)
    {
        using var workspace = new TestWorkspace();
        using var process = CancellationHostProcess.Start("credential-lease-attempt", hostedPhase, workspace.RootPath);
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var live = await new CredentialLeaseAttemptStore(new WorkspacePaths(workspace.RootPath))
                .ResumeAsync("credential-use-cross-process-1", 1);
            Assert.Equal(expectedInterruptedPhase == CredentialLeasePhase.Redeemed
                ? CredentialLeaseAttemptStoreStatus.Replayed
                : CredentialLeaseAttemptStoreStatus.OperationInProgress, live.Status);
            Assert.Equal(expectedInterruptedPhase, live.History!.Current.Phase);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            var store = new CredentialLeaseAttemptStore(new WorkspacePaths(workspace.RootPath));
            var resumed = await store.ResumeAsync("credential-use-cross-process-1", 1);
            Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, resumed.Status);
            Assert.Equal(expectedInterruptedPhase, resumed.History!.Current.Phase);
            if (expectedInterruptedPhase == CredentialLeasePhase.Redeemed)
            {
                Assert.Null(resumed.Lease);
            }
            else
            {
                var failure = expectedTerminalPhase == CredentialLeasePhase.RedemptionAmbiguous
                    ? CredentialFailureCode.OutcomeUncertain
                    : CredentialFailureCode.Unavailable;
                var terminal = CredentialLeaseContract.Advance(
                    resumed.History.Intent,
                    resumed.History.Current,
                    expectedTerminalPhase,
                    resumed.History.Current.RecordedAtUtc.AddTicks(1),
                    failureCode: failure);
                var replacement = CredentialLeaseContract.CreateHistory(resumed.History.Intent, [.. resumed.History.Versions, terminal]);
                var committed = await store.CompareExchangeAsync(resumed.History.Current.ContentHash, replacement, resumed.Lease!);
                Assert.Equal(CredentialLeaseAttemptStoreStatus.Created, committed.Status);
                resumed.Lease!.Dispose();
            }

            var replay = await new CredentialLeaseAttemptStore(new WorkspacePaths(workspace.RootPath))
                .ResumeAsync("credential-use-cross-process-1", 1);
            Assert.Equal(CredentialLeaseAttemptStoreStatus.Replayed, replay.Status);
            Assert.Equal(expectedTerminalPhase, replay.History!.Current.Phase);
            Assert.Null(replay.Lease);
            Assert.Equal(replay.History.Versions.Count, Directory.EnumerateFiles(new WorkspacePaths(workspace.RootPath).CredentialLeaseAttemptsPath, "*.json").Count());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static CredentialLeaseAttemptHistory Prepared(CredentialLeaseIntent intent)
        => CredentialLeaseContract.CreateHistory(intent, [CredentialLeaseContract.Prepare(intent, _now)]);

    private static CredentialLeaseAttemptHistory TerminalHistory(CredentialLeaseIntent intent, DateTimeOffset at)
    {
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var authorized = CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.Authorized, at, intent.Authority.CurrentAuthorityDecisionHash, RegistryEvidence(intent));
        var boundary = CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.RedemptionBoundaryReached, at.AddTicks(1));
        var terminal = CredentialLeaseContract.Advance(intent, boundary, CredentialLeasePhase.RedemptionAmbiguous, at.AddTicks(2), failureCode: CredentialFailureCode.OutcomeUncertain);
        return CredentialLeaseContract.CreateHistory(intent, [prepared, authorized, boundary, terminal]);
    }

    private static CredentialLeaseAttemptHistory NotRedeemedHistory(CredentialLeaseIntent intent, DateTimeOffset at)
    {
        var prepared = CredentialLeaseContract.Prepare(intent, _now);
        var authorized = CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.Authorized, at, intent.Authority.CurrentAuthorityDecisionHash, RegistryEvidence(intent));
        var terminal = CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.NotRedeemed, at.AddTicks(1), failureCode: CredentialFailureCode.Conflict);
        return CredentialLeaseContract.CreateHistory(intent, [prepared, authorized, terminal]);
    }

    private static CredentialUseEvidence Evidence(CredentialLeaseAttemptHistory history, CredentialScope scope)
        => new(
            CredentialUseEvidence.CurrentSchemaVersion,
            CredentialLeaseContract.ComputeEvidenceId(history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration),
            Reference(),
            ContractHash(history.Intent.Registry.BindingHash),
            Id(history.Intent.Authority.AuthorityProofId),
            Id(history.Intent.Execution.RunId),
            scope with { Target = null },
            history.Current.RecordedAtUtc,
            history.Current.Phase switch
            {
                CredentialLeasePhase.Redeemed => CredentialUseOutcome.Succeeded,
                CredentialLeasePhase.RedemptionAmbiguous => CredentialUseOutcome.OutcomeUncertain,
                _ => CredentialUseOutcome.FailedBeforeActuation,
            },
            true,
            new CredentialLeaseUseEvidence(CredentialLeaseUseEvidence.CurrentSchemaVersion, history, new RedactionSummary(RedactionStatus.Completed, 0, 0, 0, 0, 0)));

    private static CredentialLeaseAttemptHistory Append(CredentialLeaseAttemptHistory history, CredentialLeasePhase phase, DateTimeOffset at, string? authority = null, string? registry = null)
    {
        CredentialFailureCode? failure = phase == CredentialLeasePhase.NotRedeemed ? CredentialFailureCode.Conflict : null;
        var next = CredentialLeaseContract.Advance(history.Intent, history.Current, phase, at, authority, registry, failure);
        return CredentialLeaseContract.CreateHistory(history.Intent, [.. history.Versions, next]);
    }

    private static CredentialLeaseIntent Intent()
    {
        var identity = new CapabilityDescriptorIdentity(CapabilityId("org.embodysense/http/call"), CapabilityVersion("1.0.0"), CapabilityHash(Hash('c')));
        var implementation = new CapabilityImplementationIdentity(CapabilityProvider("org.embodysense"), "http/call");
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, implementation, "example-api", "raw-private-target", "read", "actor-1", _now.AddMinutes(-1), _now.AddMinutes(1));
        var binding = new CredentialCapabilityBinding(1, Reference(), Requirement("provider-token"), identity, implementation, scope);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var deadlines = new CredentialLeaseDeadlines(_now.AddMinutes(1), null, scope.NotAfterUtc, null, null, null, null, null);
        return Rehash(new CredentialLeaseIntent(
            1,
            "lease-1",
            "credential-use-1",
            1,
            new CredentialLeaseExecutionScope("workspace-1", "actor-1", Hash('1'), Hash('2'), Hash('3'), "run-1", "graph-1", "revision-1", Hash('4'), 1, "role-1", 1, Hash('5'), "loop-1", "revision-1", 1, Hash('6')),
            new CredentialLeaseAuthorityScope("proof-1", Hash('0'), "authority-1", 1, Hash('7'), "grant-1", 1, Hash('8'), Hash('9'), Hash('a'), null),
            new CredentialLeaseEffectScope("node-1", 1, "effect-1", "effect-operation-1", "idempotency-1", 1, Hash('b'), 5),
            new CredentialLeaseCapabilityScope(identity.Id.Value, identity.Version.Value, identity.Hash.Value, implementation.ProviderId.Value, implementation.ImplementationId, binding.Requirement.Name),
            new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.NotApplicable, null, null),
            new CredentialLeaseRegistryScope(binding.ReferenceId.Value, bindingHash!.Value, 7, "consent-1", Provider().Value),
            new CredentialLeaseTargetScope(scope.Service!, CredentialLeaseContract.ComputeTargetFingerprint(scope.Service!, System.Text.Encoding.UTF8.GetBytes(scope.Target!)), scope.OperationClass!, "governed provider use"),
            _now.AddSeconds(-1),
            deadlines,
            CredentialLeaseContract.ComputeEffectiveExpiry(_now.AddSeconds(-1), deadlines),
            string.Empty));
    }

    private static CredentialRegistryReadResult RegistryRead(CredentialLeaseIntent intent)
    {
        var binding = BindingFor(intent);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        Assert.Equal(intent.Registry.BindingHash, bindingHash!.Value);
        var reference = new CredentialReference(1, Reference(), "api-token", CredentialLifecycleStatus.Active, intent.Execution.ActorId, "provider access", Provider(), _now.AddDays(-1), _now, _now.AddDays(1), new Dictionary<string, string>());
        var entry = new CredentialRegistryEntry(reference, binding, bindingHash, Id("consent-1"), CredentialProviderHealthStatus.Available, 5, Id("registry-operation-1"), true);
        return new CredentialRegistryReadResult(intent.Registry.RegistryRevision, [entry], [], [], [], null);
    }

    private static CredentialCapabilityBinding BindingFor(CredentialLeaseIntent intent)
    {
        var identity = new CapabilityDescriptorIdentity(CapabilityId(intent.Capability.CapabilityId), CapabilityVersion(intent.Capability.CapabilityVersion), CapabilityHash(intent.Capability.CapabilityDescriptorHash));
        var implementation = new CapabilityImplementationIdentity(CapabilityProvider(intent.Capability.CapabilityProviderId), intent.Capability.CapabilityImplementationId);
        var scope = new CredentialScope(intent.Execution.WorkspaceId, intent.Execution.RoleId, intent.Execution.LoopId, intent.Execution.DeclaredLoopRevision, intent.Effect.NodeId, identity, implementation, intent.Target.TargetClass, "raw-private-target", intent.Target.OperationClass, intent.Execution.ActorId, _now.AddMinutes(-1), _now.AddMinutes(1));
        return new CredentialCapabilityBinding(1, Reference(), Requirement(intent.Capability.SecretRequirement), identity, implementation, scope);
    }

    private static string RegistryEvidence(CredentialLeaseIntent intent) => CredentialLeaseRegistryMatcher.Match(intent, RegistryRead(intent), _now).EvidenceHash!;
    private static CredentialLeaseIntent Rehash(CredentialLeaseIntent intent) => CredentialLeaseContract.ApplyIntentHash(intent with { ContentHash = string.Empty });
    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private static async Task<string> WriteHistoryArtifactAsync(WorkspacePaths paths, CredentialLeaseAttemptHistory history)
    {
        var existing = Path.GetFileName(Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.json").FirstOrDefault()
            ?? Directory.EnumerateFiles(paths.CredentialLeaseAttemptsPath, "*.head").Single());
        var storageKey = existing![..existing.IndexOf('.', StringComparison.Ordinal)];
        var path = Path.Combine(paths.CredentialLeaseAttemptsPath, $"{storageKey}.{history.Current.ContentHash[7..]}.json");
        await File.WriteAllBytesAsync(path, CredentialLeaseAttemptRecordCodec.Encode(history));
        return path;
    }

    private static async Task<(CredentialRegistryStore Registry, CredentialLifecycleService Lifecycle, CredentialLeaseIntent Intent)> SeedRegistryAsync(WorkspacePaths paths)
    {
        var baseline = Intent();
        var actorBound = baseline with { Execution = baseline.Execution with { ActorId = Environment.UserName } };
        var binding = BindingFor(actorBound);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        actorBound = Rehash(actorBound with { Registry = actorBound.Registry with { BindingHash = bindingHash!.Value } });
        binding = BindingFor(actorBound);
        var reference = new CredentialReference(
            CredentialReference.CurrentSchemaVersion,
            Reference(),
            "api-token",
            CredentialLifecycleStatus.Active,
            Environment.UserName,
            "governed provider access",
            Provider(),
            _now.AddDays(-1),
            _now,
            _now.AddDays(1),
            new Dictionary<string, string>());
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var trust = TestTrust(paths);
        var lifecycle = CredentialLifecyclePersistenceFactory.Create(
            paths,
            trust,
            adapter,
            provider,
            adapter,
            new CapabilityDependentIndex([adapter]),
            adapter,
            new AuditLog(paths),
            new FixedTimeProvider(_now));
        var created = await lifecycle.ExecuteAsync(
            new CredentialLifecycleRequest(
                CredentialLifecycleOperationKind.Create,
                Id("register-lease-reference"),
                Reference(),
                actorBound.Execution.WorkspaceId,
                Environment.UserName,
                0,
                _now,
                4,
                reference,
                binding,
                Id(actorBound.Registry.ConsentReferenceId)),
            destination =>
            {
                destination.Fill(1);
                return destination.Length;
            });
        Assert.True(created.Status == CredentialLifecycleResultStatus.Applied, $"create status={created.Status}; failure={created.Failure?.Code}; detail={created.Detail}");
        var consented = await lifecycle.ExecuteAsync(new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Consent,
            Id("grant-lease-consent"),
            Reference(),
            actorBound.Execution.WorkspaceId,
            Environment.UserName,
            created.RegistryRevision!.Value,
            _now,
            ConsentReference: Id(actorBound.Registry.ConsentReferenceId)));
        Assert.Equal(CredentialLifecycleResultStatus.Applied, consented.Status);
        var registry = new CredentialRegistryStore(paths, trust, adapter, new FixedTimeProvider(_now));
        var read = await registry.ReadAsync();
        Assert.True(read.Succeeded);
        var intent = Rehash(actorBound with { Registry = actorBound.Registry with { RegistryRevision = read.RegistryRevision!.Value } });
        return (registry, lifecycle, intent);
    }

    private static FileCapabilityCatalogTrustProvider TestTrust(WorkspacePaths paths)
    {
        var workspaceRoot = new DirectoryInfo(paths.WorkspacePath);
        var temporaryRoot = workspaceRoot.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
        return new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceRoot.Name, "credential-lease-registry-trust"));
    }

    private static CredentialLeaseRedemptionGate Gate(
        ICredentialRegistryStore registry,
        ICredentialLeaseAttemptStore attempts,
        CredentialLeaseAttemptHistory authorized,
        TimeProvider timeProvider,
        CredentialLeaseCurrentVerificationStatus status = CredentialLeaseCurrentVerificationStatus.Authorized)
        => new(
            registry,
            attempts,
            new CredentialLeaseCurrentAuthorityVerifier(new StaticCurrentAuthoritySource(authorized.Intent, authorized.Current.CurrentAuthorityEvidenceHash!, status)),
            new StubCapabilityAuthorityTransaction(),
            timeProvider);

    private sealed class StaticCurrentAuthoritySource(CredentialLeaseIntent intent, string evidenceHash, CredentialLeaseCurrentVerificationStatus status) : ICredentialLeaseCurrentAuthoritySnapshotSource
    {
        public Task<CredentialLeaseCurrentAuthoritySnapshot> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(status == CredentialLeaseCurrentVerificationStatus.Authorized
                ? new CredentialLeaseCurrentAuthoritySnapshot(status, intent, evidenceHash)
                : new CredentialLeaseCurrentAuthoritySnapshot(status));
    }

    private sealed class StaticRegistry(CredentialRegistryReadResult read) : ICredentialRegistryStore
    {
        public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialActorAuthentication.AuthenticatedUser);
        public ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialReferenceLookupResult.Found(read.Entries[0].Reference));
        public Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(read);
        public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialEvidenceWriteResult.Success());
        public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialEvidenceWriteResult.Success());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CredentialContractId Id(string value) { Assert.True(CredentialContractId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CredentialContractHash ContractHash(string value) { Assert.True(CredentialContractHash.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CredentialReferenceId Reference() { Assert.True(CredentialReferenceId.TryParse("reference-1", out var parsed, out _)); return parsed!; }
    private static CredentialProviderId Provider() { Assert.True(CredentialProviderId.TryParse("org.embodysense", out var parsed, out _)); return parsed!; }
    private static CapabilityId CapabilityId(string value) { Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityVersion CapabilityVersion(string value) { Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityVersion.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityDescriptorHash CapabilityHash(string value) { Assert.True(CapabilityDescriptorHash.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityProviderId CapabilityProvider(string value) { Assert.True(CapabilityProviderId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilitySecretRequirement Requirement(string value) { Assert.True(CapabilitySecretRequirement.TryParse(value, out var parsed, out _)); return parsed!; }
}
