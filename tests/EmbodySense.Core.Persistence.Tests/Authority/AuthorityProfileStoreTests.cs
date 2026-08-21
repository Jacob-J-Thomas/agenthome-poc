using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Authority;

public sealed class AuthorityProfileStoreTests : IDisposable
{
    // See https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422: covered Windows verification can delay entry into the injected durability barrier.
    private static readonly TimeSpan _durabilityBarrierEntryTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, false) } };
    private static readonly JsonSerializerOptions _canonicalDocumentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, false) }
    };
    private readonly TestWorkspace _trustRoot = new();
    private readonly FileCapabilityCatalogTrustProvider _trustProvider;

    public AuthorityProfileStoreTests()
    {
        _trustProvider = new FileCapabilityCatalogTrustProvider(_trustRoot.RootPath);
    }

    public void Dispose()
    {
        _trustRoot.Dispose();
    }

    [Fact]
    public async Task Create_revise_transition_tombstone_and_restart_preserve_immutable_hashed_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = Profile();

        var created = await store.MutateAsync(Create(profile, "create-profile"));
        var revisedProfile = profile with { Revision = Revision(2), Purpose = Purpose("Inspect a bounded workspace after an explicit user correction.") };
        var revised = await store.MutateAsync(Revise(revisedProfile, "revise-profile"));
        var transitioned = await store.MutateAsync(Transition(profile.ProfileId, 2, AuthorityProfileStatus.Suspended, "suspend-profile"));
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, 3, "tombstone-profile"));
        var restartRead = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, revised.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, transitioned.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, restartRead.Status);
        var record = Assert.IsType<AuthorityProfileRecord>(restartRead.Record);
        Assert.Equal(3, record.Revisions.Count);
        Assert.Equal(AuthorityProfileStatus.Suspended, record.CurrentProfile.Status);
        Assert.NotNull(record.Tombstone);
        Assert.Equal(4, record.Operations.Count);
        Assert.All(record.Revisions, revision => Assert.True(AuthorityProfileHash.TryCompute(revision.Profile, out var hash, out _)));
        Assert.Equal(record.Revisions.Select(revision => revision.Hash.Value), record.Revisions.Select(revision => AuthorityProfileHash.TryCompute(revision.Profile, out var hash, out _) ? hash!.Value : null));
        Assert.DoesNotContain("private-target", await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_is_exact_while_changed_intent_stale_revision_and_resurrection_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var profile = Profile();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var mutation = Create(profile, "same-operation");

        var created = await store.MutateAsync(mutation);
        var transitioned = await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "later-operation"));
        var replayed = await store.MutateAsync(mutation);
        var changedIntent = await store.MutateAsync(Transition(profile.ProfileId, 2, AuthorityProfileStatus.Active, "same-operation"));
        var stale = await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Retired, "stale-operation"));
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, 2, "tombstone-operation"));
        var transitionReplay = await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "later-operation"));
        var tombstoneReplay = await store.MutateAsync(Tombstone(profile.ProfileId, 2, "tombstone-operation"));
        var resurrection = await store.MutateAsync(Revise(profile with { Revision = Revision(3) }, "resurrect-operation"));

        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, transitioned.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Replayed, replayed.Status);
        Assert.Equal(1, replayed.Record!.CurrentProfile.Revision.Value);
        Assert.Equal(AuthorityProfileMutationStatus.Conflict, changedIntent.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Conflict, stale.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.Null(tombstoned.Record!.Operations.Single(operation => operation.OperationId == "tombstone-operation").ResultingRevision);
        Assert.Equal(AuthorityProfileMutationStatus.Replayed, transitionReplay.Status);
        Assert.Null(transitionReplay.Record!.Tombstone);
        Assert.DoesNotContain(transitionReplay.Record.Operations, operation => operation.OperationId == "tombstone-operation");
        Assert.Equal(AuthorityProfileMutationStatus.Replayed, tombstoneReplay.Status);
        Assert.Null(tombstoneReplay.Record!.Operations.Single(operation => operation.OperationId == "tombstone-operation").ResultingRevision);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, resurrection.Status);
    }

    [Fact]
    public async Task Invalid_duplicate_and_missing_inputs_fail_closed_without_creating_operation_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = Profile();

        var invalidRead = await store.ReadAsync("not a profile id");
        var missingRead = await store.ReadAsync("missing-profile");
        var nullMutation = await store.MutateAsync(null!);
        var missingMutation = await store.MutateAsync(Transition(ProfileId("missing-profile"), 1, AuthorityProfileStatus.Suspended, "transition-missing"));
        var created = await store.MutateAsync(Create(profile, "create-once"));
        var duplicate = await store.MutateAsync(Create(profile, "create-twice"));
        var invalidCreate = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.Create, "create-invalid-revision", 1, profile with { Revision = Revision(2) }, null, null, Actor(), Reason()));
        var overflowingRevision = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.Create, "create-overflowing-revision", int.MaxValue, profile, null, null, Actor(), Reason()));
        var invalidTransition = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.TransitionStatus, "transition-without-status", 1, null, profile.ProfileId, null, Actor(), Reason()));
        var persisted = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, invalidRead.Status);
        Assert.Equal(AuthorityProfileReadStatus.NotFound, missingRead.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, nullMutation.Status);
        Assert.Equal(AuthorityProfileMutationStatus.NotFound, missingMutation.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, duplicate.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, invalidCreate.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, overflowingRevision.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, invalidTransition.Status);
        Assert.Single(persisted.Record!.Operations);
        Assert.Equal("create-once", persisted.Record.Operations[0].OperationId);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("-x")]
    [InlineData("x-")]
    public async Task Profile_mutations_reject_operation_ids_without_alphanumeric_boundaries(string operationId)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var result = await Store(paths).MutateAsync(Create(Profile(), operationId));

        Assert.Equal(AuthorityProfileMutationStatus.Invalid, result.Status);
        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
        Assert.False(File.Exists(paths.AuthorityProfilesProofPath));
    }

    [Fact]
    public async Task Profile_mutations_accept_canonical_operation_ids_with_internal_punctuation()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));

        var result = await store.MutateAsync(Create(Profile(), "create.profile_revision-1"));

        Assert.Equal(AuthorityProfileMutationStatus.Applied, result.Status);
        Assert.Equal("create.profile_revision-1", result.OperationId);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("non-utc")]
    [InlineData("throwing")]
    public async Task Invalid_trusted_clocks_fail_before_profile_evidence_is_written(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        TimeProvider timeProvider = scenario switch
        {
            "default" => new StubTimeProvider(default),
            "non-utc" => new StubTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(1))),
            _ => new ThrowingTimeProvider()
        };
        var store = new AuthorityProfileStore(paths, _trustProvider, timeProvider);

        var result = await store.MutateAsync(Create(Profile(), "invalid-clock-create"));

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
        Assert.False(File.Exists(paths.AuthorityProfilesProofPath));
    }

    [Fact]
    public async Task Each_profile_mutation_uses_one_coherent_trusted_operation_time()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(1);
        var third = second.AddMinutes(1);
        var timeProvider = new SequenceTimeProvider(first, second, third);
        var store = new AuthorityProfileStore(paths, _trustProvider, timeProvider);
        var profile = Profile();

        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Create(profile, "coherent-create"))).Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "coherent-suspend"))).Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Tombstone(profile.ProfileId, 2, "coherent-tombstone"))).Status);
        var read = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(3, timeProvider.CallCount);
        Assert.Equal(first, read.Record!.Revisions[0].RecordedAtUtc);
        Assert.Equal(first, read.Record.Operations.Single(operation => operation.OperationId == "coherent-create").RecordedAtUtc);
        Assert.Equal(second, read.Record.Revisions[1].RecordedAtUtc);
        Assert.Equal(second, read.Record.Operations.Single(operation => operation.OperationId == "coherent-suspend").RecordedAtUtc);
        Assert.Equal(third, read.Record.Tombstone!.RecordedAtUtc);
        Assert.Equal(third, read.Record.Operations.Single(operation => operation.OperationId == "coherent-tombstone").RecordedAtUtc);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("throwing")]
    public async Task Exact_profile_replay_is_clock_independent_and_writes_no_new_evidence(string failureMode)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeProvider = new FailAfterFirstTimeProvider(
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            failureMode);
        var store = new AuthorityProfileStore(paths, _trustProvider, timeProvider);
        var mutation = Create(Profile(), "clock-independent-replay");
        var committed = await store.MutateAsync(mutation);
        var beforeReplay = await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath);

        var replayed = await store.MutateAsync(mutation);
        var afterReplay = await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath);

        Assert.Equal(AuthorityProfileMutationStatus.Applied, committed.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Record!.CurrentProfile, replayed.Record!.CurrentProfile);
        Assert.Equal(committed.Record.Operations.Single(), replayed.Record.Operations.Single());
        Assert.Equal(1, timeProvider.CallCount);
        Assert.Equal(beforeReplay, afterReplay);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_attempting_a_read_or_mutation()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new AuthorityProfileStore(paths);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("missing-profile", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutateAsync(Create(Profile(), "cancelled-create"), cancellation.Token));

        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
        Assert.False(File.Exists(paths.AuthorityProfilesProofPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes / 6 + 1)]
    public void Constructor_rejects_unbounded_trust_authentication_tags(int maximumTagBytes)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider { MaximumAuthenticationTagUtf8Bytes = maximumTagBytes };

        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityProfileStore(paths, trust));
    }

    [Fact]
    public async Task Reads_and_mutations_execute_inside_the_shared_workspace_authority_fence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var retained = await authority.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));
        Assert.NotNull(retained);
        var probe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var store = new AuthorityProfileStore(paths, _trustProvider, authorityTransaction: probe);

        var mutation = Task.Run(() => store.MutateAsync(Create(Profile(), "fenced-profile-create")));
        await probe.Attempted.Task;

        Assert.False(mutation.IsCompleted);
        await retained!.DisposeAsync();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await mutation).Status);

        var readProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var read = await new AuthorityProfileStore(paths, _trustProvider, authorityTransaction: readProbe).ReadAsync("workspace-observer");
        Assert.True(readProbe.Attempted.Task.IsCompleted);
        Assert.Equal(AuthorityProfileReadStatus.Available, read.Status);
    }

    [Fact]
    public async Task Invalid_utf8_artifacts_recover_only_the_last_proved_profile()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-invalid-utf8"))).Status);

        await File.WriteAllBytesAsync(paths.AuthorityProfilesDocumentPath, [0xc3, 0x28]);
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesProofPath, [0xc3, 0x28]);
        var unavailable = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.NotFound, recovered.Status);
        Assert.Null(recovered.Record);
        Assert.Equal(AuthorityProfileReadStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Missing_current_and_proof_artifacts_never_become_a_mutation_base()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-artifact-loss"))).Status);
        File.Delete(paths.AuthorityProfilesDocumentPath);
        File.Delete(paths.AuthorityProfilesProofPath);

        var read = await Store(paths).ReadAsync(profile.ProfileId.Value);
        var mutation = await Store(paths).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-after-artifact-loss"));

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, read.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, mutation.Status);
    }

    [Fact]
    public async Task Trust_read_failure_returns_only_unavailable_results()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        var failing = new FaultInjectingTrustProvider(_trustProvider) { FailRead = true };
        var store = new AuthorityProfileStore(paths, failing);

        var read = await store.ReadAsync(profile.ProfileId.Value);
        var mutation = await store.MutateAsync(Create(profile, "create-with-unavailable-trust"));

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, read.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, mutation.Status);
        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
    }

    [Fact]
    public async Task Unreadable_authenticated_artifacts_are_not_used_as_current_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-verification-failure"))).Status);
        var failing = new FaultInjectingTrustProvider(_trustProvider) { FailVerificationWithFormat = true };

        var read = await new AuthorityProfileStore(paths, failing).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, read.Status);
    }

    [Fact]
    public async Task Profile_quota_rejects_a_new_declaration_without_evicting_existing_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider();
        var store = new AuthorityProfileStore(paths, trust);
        await SeedMaximumProfilesAsync(paths, trust, store);

        var rejected = await store.MutateAsync(Create(Profile("profile-overflow"), "create-profile-overflow"));
        var retained = await new AuthorityProfileStore(paths, trust).ReadAsync("profile-031");
        var overflow = await new AuthorityProfileStore(paths, trust).ReadAsync("profile-overflow");

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, retained.Status);
        Assert.Equal("profile-031", retained.Record!.ProfileId.Value);
        Assert.Equal(AuthorityProfileReadStatus.NotFound, overflow.Status);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_last_proved_state_and_substituted_workspace_is_unavailable()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var profile = Profile();
        var created = await Store(sourcePaths).MutateAsync(Create(profile, "create-source"));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(sourcePaths).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "advance-source"))).Status);

        var targetPaths = new WorkspacePaths(target.RootPath);
        Directory.CreateDirectory(targetPaths.AuthorityProfilesPath);
        File.Copy(sourcePaths.AuthorityProfilesDocumentPath, targetPaths.AuthorityProfilesDocumentPath, true);
        var substituted = await Store(targetPaths).ReadAsync(profile.ProfileId.Value);
        await File.WriteAllTextAsync(sourcePaths.AuthorityProfilesDocumentPath, "{partial");
        var recovered = await Store(sourcePaths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, substituted.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(profile.ProfileId, recovered.Record!.ProfileId);
    }

    [Fact]
    public async Task Null_operation_entries_recover_from_the_last_proved_profile_or_return_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        var store = Store(paths);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Create(profile, "create-null-operation"))).Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-null-operation"))).Status);
        var malformed = await CreateAuthenticatedNullOperationDocumentAsync(paths);

        await WriteDocumentAsync(paths.AuthorityProfilesDocumentPath, malformed);
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);
        await WriteDocumentAsync(paths.AuthorityProfilesProofPath, malformed);
        var unavailable = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(1, recovered.Record!.CurrentProfile.Revision.Value);
        Assert.Equal(AuthorityProfileReadStatus.Unavailable, unavailable.Status);
    }

    [Theory]
    [InlineData("orphan-target")]
    [InlineData("wrong-revision")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-time")]
    [InlineData("transition-payload-splice")]
    [InlineData("missing-receipt")]
    [InlineData("extra-receipt")]
    [InlineData("tombstone-splice")]
    public async Task Authenticated_profile_operation_lineage_corruption_fails_closed(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider();
        var first = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var store = new AuthorityProfileStore(paths, trust, new SequenceTimeProvider(first, first.AddMinutes(1), first.AddMinutes(2)));
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Create(profile, "correlation-create"))).Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "correlation-suspend"))).Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Tombstone(profile.ProfileId, 2, "correlation-tombstone"))).Status);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))!.AsObject();
        var profileDocument = document["profiles"]![0]!.AsObject();
        var revisions = profileDocument["revisions"]!.AsArray();
        var operations = document["operations"]!.AsArray();
        var transitionOperation = operations.Single(node => node!["operationId"]!.GetValue<string>() == "correlation-suspend")!.AsObject();

        switch (scenario)
        {
            case "orphan-target":
                transitionOperation["profileId"] = "orphan-profile";
                break;
            case "wrong-revision":
                transitionOperation["resultingRevision"] = 99;
                break;
            case "wrong-kind":
                transitionOperation["kind"] = "create";
                break;
            case "wrong-operation":
                revisions[1]!["operationId"] = "missing-revision-operation";
                break;
            case "wrong-time":
                revisions[1]!["recordedAtUtc"] = first.AddMinutes(1).AddSeconds(1);
                break;
            case "transition-payload-splice":
                var forged = profile with
                {
                    Revision = Revision(2),
                    Status = AuthorityProfileStatus.Suspended,
                    Purpose = Purpose("Forged authority purpose hidden behind a status transition.")
                };
                Assert.True(AuthorityProfileJson.TrySerialize(forged, out var forgedJson, out _));
                Assert.True(AuthorityProfileHash.TryCompute(forged, out var forgedHash, out _));
                revisions[1]!["profileJson"] = forgedJson;
                revisions[1]!["profileHash"] = forgedHash!.Value;
                break;
            case "missing-receipt":
                operations.Remove(transitionOperation);
                document["generation"] = 2L;
                break;
            case "extra-receipt":
                var extra = transitionOperation.DeepClone().AsObject();
                extra["operationId"] = "zz-extra-operation";
                operations.Add(extra);
                document["generation"] = 4L;
                break;
            default:
                profileDocument["tombstone"]!["operationId"] = "correlation-suspend";
                break;
        }

        await ReplaceWithAuthenticatedDocumentAsync(paths, trust, document);

        var read = await new AuthorityProfileStore(paths, trust).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, read.Status);
        Assert.Null(read.Record);
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_expected_revision_are_serialized_by_cross_process_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = Store(paths);
        var second = Store(paths);
        var results = await Task.WhenAll(first.MutateAsync(Create(Profile("first-profile"), "create-first")), second.MutateAsync(Create(Profile("second-profile"), "create-second")));

        Assert.Equal(2, results.Count(result => result.Status == AuthorityProfileMutationStatus.Applied));
        Assert.Equal(AuthorityProfileReadStatus.Available, (await first.ReadAsync("first-profile")).Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, (await second.ReadAsync("second-profile")).Status);
    }

    [Fact]
    public async Task Revision_quota_accepts_128_rejects_129_without_poisoning_read_or_tombstone_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider();
        var store = new AuthorityProfileStore(paths, trust);
        var profile = Profile();
        await SeedProfileRevisionsBelowLimitAsync(paths, trust, store, profile);
        var accepted = await store.MutateAsync(Transition(
            profile.ProfileId,
            AuthorityProfileStoreLimits.MaximumRevisionsPerProfile - 1,
            AuthorityProfileStatus.Suspended,
            $"revision-{AuthorityProfileStoreLimits.MaximumRevisionsPerProfile}"));

        var atLimit = await new AuthorityProfileStore(paths, trust).ReadAsync(profile.ProfileId.Value);
        var rejected = await store.MutateAsync(Transition(profile.ProfileId, AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, AuthorityProfileStatus.Retired, "revision-129"));
        var afterRejected = await new AuthorityProfileStore(paths, trust).ReadAsync(profile.ProfileId.Value);
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, "tombstone-at-revision-limit"));
        var afterTombstone = await new AuthorityProfileStore(paths, trust).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Applied, accepted.Status);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, atLimit.Record!.Revisions.Count);
        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, afterRejected.Status);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, afterRejected.Record!.CurrentProfile.Revision.Value);
        Assert.DoesNotContain(afterRejected.Record.Operations, operation => operation.OperationId == "revision-129");
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.NotNull(afterTombstone.Record!.Tombstone);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, afterTombstone.Record.CurrentProfile.Revision.Value);
    }

    [Fact]
    public async Task Mutation_does_not_report_applied_before_both_authority_artifacts_cross_the_durability_barrier()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-barrier"))).Status);
        var barrier = new BlockingAuthorityProfileDurabilityBarrier();
        var store = new AuthorityProfileStore(paths, _trustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-behind-barrier"));
        await barrier.Entered.WaitAsync(_durabilityBarrierEntryTimeout);

        Assert.False(mutation.IsCompleted);
        barrier.Release();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await mutation).Status);
        Assert.Equal(2, barrier.CallCount);
    }

    [Fact]
    public async Task Candidate_rename_durability_failure_returns_unavailable_and_recovers_prior_proved_profile()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-rename-failure"))).Status);
        var barrier = new BlockingAuthorityProfileDurabilityBarrier { TargetCall = 2, Failure = new IOException("Injected candidate durability failure.") };
        var store = new AuthorityProfileStore(paths, _trustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-with-rename-failure"));
        await barrier.Entered.WaitAsync(_durabilityBarrierEntryTimeout);
        Assert.False(mutation.IsCompleted);
        barrier.Release();
        var failed = await mutation;
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, failed.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(1, recovered.Record!.CurrentProfile.Revision.Value);
        Assert.Equal(AuthorityProfileStatus.Active, recovered.Record.CurrentProfile.Status);
        Assert.DoesNotContain(recovered.Record.Operations, operation => operation.OperationId == "transition-with-rename-failure");
    }

    [Fact]
    public async Task Trust_anchor_advance_failure_returns_unavailable_and_preserves_prior_read_only_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-anchor-failure"))).Status);
        var failing = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };

        var failed = await new AuthorityProfileStore(paths, failing).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-before-anchor-failure"));
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, failed.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(1, recovered.Record!.CurrentProfile.Revision.Value);
        Assert.DoesNotContain(recovered.Record.Operations, operation => operation.OperationId == "transition-before-anchor-failure");
    }

    [Theory]
    [InlineData(UnexpectedAdvanceMode.NoOp)]
    [InlineData(UnexpectedAdvanceMode.Stale)]
    [InlineData(UnexpectedAdvanceMode.WrongWorkspace)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentDigest)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousDigest)]
    public async Task Profile_mutation_does_not_report_a_candidate_when_advance_returns_an_unproved_successor(UnexpectedAdvanceMode mode)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var unexpectedTrust = new UnexpectedAdvanceTrustProvider(_trustProvider, mode);

        var result = await new AuthorityProfileStore(paths, unexpectedTrust).MutateAsync(Create(Profile(), "reject-unproved-profile-successor"));

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, result.Status);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task Windows_external_lock_owner_blocks_mutation_until_ownership_is_released()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-owner-contention"))).Status);
        using var ownership = new FileStream(paths.AuthorityProfilesLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var mutation = Store(paths).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-after-owner-contention"));
        await Task.Delay(100);
        Assert.False(mutation.IsCompleted);
        ownership.Dispose();

        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await mutation).Status);
    }

    private AuthorityProfileStore Store(WorkspacePaths paths) => new(paths, _trustProvider);

    private static async Task SeedMaximumProfilesAsync(
        WorkspacePaths paths,
        MutableAuthenticatedTrustProvider trust,
        AuthorityProfileStore store)
    {
        var firstProfile = Profile("profile-000");
        Assert.Equal(
            AuthorityProfileMutationStatus.Applied,
            (await store.MutateAsync(Create(firstProfile, "create-profile-000"))).Status);

        var initial = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))!.AsObject();
        var workspaceIdentity = initial["workspaceIdentity"]!.GetValue<string>();
        var recordedAtUtc = initial["operations"]![0]!["recordedAtUtc"]!.GetValue<DateTimeOffset>();
        var profiles = new List<AuthorityProfileSeedDocument>(AuthorityProfileStoreLimits.MaximumProfiles);
        var operations = new List<AuthorityProfileOperationSeedDocument>(AuthorityProfileStoreLimits.MaximumProfiles);
        for (var index = 0; index < AuthorityProfileStoreLimits.MaximumProfiles; index++)
        {
            var profile = Profile($"profile-{index:D3}");
            var operation = Create(profile, $"create-profile-{index:D3}");
            Assert.True(AuthorityProfileJson.TrySerialize(profile, out var profileJson, out _));
            Assert.True(AuthorityProfileHash.TryCompute(profile, out var profileHash, out _));
            profiles.Add(new AuthorityProfileSeedDocument(
                profile.ProfileId.Value,
                [new AuthorityProfileRevisionSeedDocument(1, profileJson!, profileHash!.Value, operation.OperationId, recordedAtUtc)],
                null));
            operations.Add(new AuthorityProfileOperationSeedDocument(
                operation.OperationId,
                ComputeProfileRequestHash(operation),
                operation.Kind,
                AuthorityProfileMutationStatus.Applied,
                profile.ProfileId.Value,
                1,
                operation.ActorId.Value,
                operation.Reason.Value,
                recordedAtUtc));
        }

        var document = new AuthorityProfileStoreSeedDocument(
            1,
            workspaceIdentity,
            AuthorityProfileStoreLimits.MaximumProfiles,
            profiles,
            operations,
            [],
            [],
            string.Empty,
            string.Empty);
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        var authenticated = document with
        {
            ContentDigest = digest,
            AuthenticationTag = MutableAuthenticatedTrustProvider.AuthenticationTag
        };
        var json = JsonSerializer.Serialize(authenticated, _jsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, json);
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, json);
        trust.SetCurrent(workspaceIdentity, document.Generation, digest);
    }

    private static async Task SeedProfileRevisionsBelowLimitAsync(
        WorkspacePaths paths,
        MutableAuthenticatedTrustProvider trust,
        AuthorityProfileStore store,
        AuthorityProfile initialProfile)
    {
        Assert.Equal(
            AuthorityProfileMutationStatus.Applied,
            (await store.MutateAsync(Create(initialProfile, "revision-1"))).Status);
        Assert.Equal(
            AuthorityProfileMutationStatus.Applied,
            (await store.MutateAsync(Transition(initialProfile.ProfileId, 1, AuthorityProfileStatus.Suspended, "revision-2"))).Status);

        var publicBytes = await File.ReadAllBytesAsync(paths.AuthorityProfilesDocumentPath);
        var publicDocument = JsonNode.Parse(publicBytes)!.AsObject();
        var workspaceIdentity = publicDocument["workspaceIdentity"]!.GetValue<string>();
        var recordedAtByOperation = publicDocument["operations"]!
            .AsArray()
            .ToDictionary(
                node => node!["operationId"]!.GetValue<string>(),
                node => node!["recordedAtUtc"]!.GetValue<DateTimeOffset>(),
                StringComparer.Ordinal);
        var retainedRecordedAtUtc = recordedAtByOperation["revision-2"];
        var publicFixture = CreateProfileRevisionSeedDocument(
            workspaceIdentity,
            initialProfile,
            2,
            operationId => recordedAtByOperation[operationId]);
        Assert.Equal(publicBytes, SerializeAuthenticatedProfileSeedDocument(publicFixture));

        var maximumFixture = CreateProfileRevisionSeedDocument(
            workspaceIdentity,
            initialProfile,
            AuthorityProfileStoreLimits.MaximumRevisionsPerProfile - 1,
            _ => retainedRecordedAtUtc);
        var maximumBytes = SerializeAuthenticatedProfileSeedDocument(maximumFixture);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesDocumentPath, maximumBytes);
        await File.WriteAllBytesAsync(paths.AuthorityProfilesProofPath, maximumBytes);
        trust.SetCurrent(workspaceIdentity, maximumFixture.Generation, maximumFixture.ContentDigest);

        var proved = await new AuthorityProfileStore(paths, trust).ReadAsync(initialProfile.ProfileId.Value);
        Assert.Equal(AuthorityProfileReadStatus.Available, proved.Status);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile - 1, proved.Record!.Revisions.Count);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile - 1, proved.Record.Operations.Count);
    }

    private static AuthorityProfileStoreSeedDocument CreateProfileRevisionSeedDocument(
        string workspaceIdentity,
        AuthorityProfile initialProfile,
        int maximumRevision,
        Func<string, DateTimeOffset> recordedAt)
    {
        var revisions = new List<AuthorityProfileRevisionSeedDocument>(maximumRevision);
        var operations = new List<AuthorityProfileOperationSeedDocument>(maximumRevision);
        var current = initialProfile;
        for (var revision = 1; revision <= maximumRevision; revision++)
        {
            var operationId = $"revision-{revision}";
            AuthorityProfileMutation mutation;
            if (revision == 1)
            {
                mutation = Create(current, operationId);
            }
            else
            {
                var status = revision % 2 == 0 ? AuthorityProfileStatus.Suspended : AuthorityProfileStatus.Active;
                mutation = Transition(initialProfile.ProfileId, revision - 1, status, operationId);
                current = current with { Revision = Revision(revision), Status = status };
            }

            Assert.True(AuthorityProfileJson.TrySerialize(current, out var profileJson, out _));
            Assert.True(AuthorityProfileHash.TryCompute(current, out var profileHash, out _));
            var operationRecordedAtUtc = recordedAt(operationId);
            revisions.Add(new AuthorityProfileRevisionSeedDocument(revision, profileJson!, profileHash!.Value, operationId, operationRecordedAtUtc));
            operations.Add(new AuthorityProfileOperationSeedDocument(
                operationId,
                ComputeProfileMutationRequestHash(mutation),
                mutation.Kind,
                AuthorityProfileMutationStatus.Applied,
                initialProfile.ProfileId.Value,
                revision,
                mutation.ActorId.Value,
                mutation.Reason.Value,
                operationRecordedAtUtc));
        }

        var document = new AuthorityProfileStoreSeedDocument(
            1,
            workspaceIdentity,
            maximumRevision,
            [new AuthorityProfileSeedDocument(initialProfile.ProfileId.Value, revisions, null)],
            operations.OrderBy(operation => operation.OperationId, StringComparer.Ordinal).ToArray(),
            [],
            [],
            string.Empty,
            string.Empty);
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        return document with
        {
            ContentDigest = digest,
            AuthenticationTag = MutableAuthenticatedTrustProvider.AuthenticationTag
        };
    }

    private static byte[] SerializeAuthenticatedProfileSeedDocument(AuthorityProfileStoreSeedDocument document)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _canonicalDocumentJsonOptions) + Environment.NewLine);

    private static string ComputeProfileMutationRequestHash(AuthorityProfileMutation mutation)
    {
        var profileJson = mutation.Profile is null
            ? string.Empty
            : AuthorityProfileJson.TrySerialize(mutation.Profile, out var json, out _)
                ? json!
                : string.Empty;
        var content = $"{(int)mutation.Kind}\n{mutation.OperationId}\n{mutation.ExpectedRevision}\n{mutation.ProfileId?.Value ?? mutation.Profile?.ProfileId.Value}\n{(int?)mutation.Status}\n{profileJson}\n{mutation.ActorId.Value}\n{mutation.Reason.Value}";
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private static string ComputeProfileRequestHash(AuthorityProfileMutation mutation)
    {
        Assert.NotNull(mutation.Profile);
        Assert.True(AuthorityProfileJson.TrySerialize(mutation.Profile, out var profileJson, out _));
        var content = $"{(int)mutation.Kind}\n{mutation.OperationId}\n{mutation.ExpectedRevision}\n{mutation.Profile.ProfileId.Value}\n{(int?)mutation.Status}\n{profileJson}\n{mutation.ActorId.Value}\n{mutation.Reason.Value}";
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private sealed record AuthorityProfileStoreSeedDocument(
        int SchemaVersion,
        string WorkspaceIdentity,
        long Generation,
        IReadOnlyList<AuthorityProfileSeedDocument> Profiles,
        IReadOnlyList<AuthorityProfileOperationSeedDocument> Operations,
        IReadOnlyList<object> Grants,
        IReadOnlyList<object> GrantOperations,
        string ContentDigest,
        string AuthenticationTag);

    private sealed record AuthorityProfileSeedDocument(
        string ProfileId,
        IReadOnlyList<AuthorityProfileRevisionSeedDocument> Revisions,
        object? Tombstone);

    private sealed record AuthorityProfileRevisionSeedDocument(
        int Revision,
        string ProfileJson,
        string ProfileHash,
        string OperationId,
        DateTimeOffset RecordedAtUtc);

    private sealed record AuthorityProfileOperationSeedDocument(
        string OperationId,
        string RequestHash,
        AuthorityProfileMutationKind Kind,
        AuthorityProfileMutationStatus Outcome,
        string ProfileId,
        int? ResultingRevision,
        string ActorId,
        string Reason,
        DateTimeOffset RecordedAtUtc);

    private async Task<JsonObject> CreateAuthenticatedNullOperationDocumentAsync(WorkspacePaths paths)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))?.AsObject();
        Assert.NotNull(document);
        document["operations"] = new JsonArray((JsonNode?)null);
        document["generation"] = document["generation"]!.GetValue<long>() + 1;
        document["contentDigest"] = string.Empty;
        document["authenticationTag"] = string.Empty;
        var contentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        var workspaceIdentity = document["workspaceIdentity"]!.GetValue<string>();
        var generation = document["generation"]!.GetValue<long>();
        document["contentDigest"] = contentDigest;
        document["authenticationTag"] = await _trustProvider.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest);
        return document;
    }

    private static async Task ReplaceWithAuthenticatedDocumentAsync(
        WorkspacePaths paths,
        MutableAuthenticatedTrustProvider trust,
        JsonObject document)
    {
        document["contentDigest"] = string.Empty;
        document["authenticationTag"] = string.Empty;
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        var identity = document["workspaceIdentity"]!.GetValue<string>();
        var generation = document["generation"]!.GetValue<long>();
        document["contentDigest"] = digest;
        document["authenticationTag"] = MutableAuthenticatedTrustProvider.AuthenticationTag;
        trust.SetCurrent(identity, generation, digest);
        var json = JsonSerializer.Serialize(document, _jsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, json);
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, json);
    }

    private static Task WriteDocumentAsync(string path, JsonObject document) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, _jsonOptions) + Environment.NewLine);

    private static AuthorityProfileMutation Create(AuthorityProfile profile, string operationId) => new(AuthorityProfileMutationKind.Create, operationId, 0, profile, null, null, Actor(), Reason());
    private static AuthorityProfileMutation Revise(AuthorityProfile profile, string operationId) => new(AuthorityProfileMutationKind.Revise, operationId, profile.Revision.Value - 1, profile, null, null, Actor(), Reason());
    private static AuthorityProfileMutation Transition(AuthorityProfileId id, int expectedRevision, AuthorityProfileStatus status, string operationId) => new(AuthorityProfileMutationKind.TransitionStatus, operationId, expectedRevision, null, id, status, Actor(), Reason());
    private static AuthorityProfileMutation Tombstone(AuthorityProfileId id, int expectedRevision, string operationId) => new(AuthorityProfileMutationKind.Tombstone, operationId, expectedRevision, null, id, null, Actor(), Reason());

    private static AuthorityProfile Profile(string id = "workspace-observer") => new(AuthorityProfile.CurrentSchemaVersion, ProfileId(id), Revision(1), AuthorityProfileStatus.Active, Purpose("Inspect bounded workspace state for one user-directed task."), new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), null, new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.ReadOnly, false, false, false), []);
    private static AuthorityProfileId ProfileId(string value)
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static AuthorityProfileRevision Revision(int value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsed, out _));
        return parsed!;
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var parsed, out _));
        return parsed!;
    }

    private static AuthorityPurpose Reason() => Purpose("User-directed lifecycle record.");
    private static AuthorityPurpose Purpose(string value)
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private sealed class StubTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Injected clock failure.");
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        internal int CallCount => _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            if (index >= values.Length)
            {
                throw new InvalidOperationException("The trusted clock was read more than once per operation.");
            }

            return values[index];
        }
    }

    private sealed class FailAfterFirstTimeProvider(DateTimeOffset first, string failureMode) : TimeProvider
    {
        private int _callCount;

        internal int CallCount => _callCount;

        public override DateTimeOffset GetUtcNow()
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                return first;
            }

            return failureMode == "default"
                ? default
                : throw new InvalidOperationException("Injected replay clock failure.");
        }
    }

    private sealed class MutableAuthenticatedTrustProvider : ICapabilityCatalogTrustProvider
    {
        internal const string AuthenticationTag = "authenticated-test-artifact";

        private CapabilityCatalogTrustState? _state;

        public int MaximumAuthenticationTagUtf8Bytes { get; init; } = 64;

        public void RequireDisjointWorkspace(string workspaceRootPath)
        {
        }

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
        {
            _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
            return Task.FromResult(_state);
        }

        public Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AuthenticationTag);

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            string authenticationTag,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(authenticationTag, AuthenticationTag, StringComparison.Ordinal));

        public Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
        {
            _state = new CapabilityCatalogTrustState(workspaceIdentity, newGeneration, newContentDigest, expectedGeneration, expectedContentDigest);
            return Task.FromResult(_state);
        }

        internal void SetCurrent(string workspaceIdentity, long generation, string contentDigest)
            => _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
    }

    private sealed class FaultInjectingTrustProvider(ICapabilityCatalogTrustProvider inner) : ICapabilityCatalogTrustProvider
    {
        public bool FailRead { get; init; }

        public bool FailVerificationWithFormat { get; init; }

        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
            => FailRead ? Task.FromException<CapabilityCatalogTrustState?>(new IOException("Injected trust read failure.")) : inner.ReadAsync(workspaceIdentity, cancellationToken);

        public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
            => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
            => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
            => FailVerificationWithFormat ? Task.FromException<bool>(new FormatException("Injected artifact verification failure.")) : inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
            => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }
}
