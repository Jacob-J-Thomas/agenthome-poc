using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace EmbodySense.Core.Application.Tests.Loops.Revisions;

[Collection(Verification.ApplicationSerialStateCollection.Name)]
public sealed class GovernedLoopRevisionLifecycleServiceTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly string _authorityHash = Hash('a');
    private static readonly string _validationHash = Hash('b');

    [Fact]
    public async Task Invalid_request_is_bounded_read_only_and_never_reaches_authority_or_persistence()
    {
        var store = new InMemoryStore();
        var authorizer = new StubAuthorizer();
        var service = Service(store, authorizer);

        var result = await service.MutateAsync(null);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Invalid, result.Status);
        Assert.Equal(string.Empty, result.OperationId);
        Assert.Equal(string.Empty, result.RequestHash);
        Assert.Single(result.ValidationErrors);
        Assert.IsAssignableFrom<IReadOnlyList<GovernedLoopRevisionLifecycleValidationError>>(result.ValidationErrors);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopRevisionLifecycleValidationError>)result.ValidationErrors).Add(result.ValidationErrors[0]));
        Assert.Equal(0, authorizer.Calls);
        Assert.Equal(0, store.MutationReads);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public void Request_hash_is_culture_independent_field_sensitive_and_contains_no_client_authority_surface()
    {
        var revision = Revision("graph-a", "revision-1", '1');
        var request = Request(GovernedLoopRevisionOperationKind.CreateDraft, "operation-1", null, candidate: revision);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = GovernedLoopRevisionLifecycleRequestHash.Compute(request);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = GovernedLoopRevisionLifecycleRequestHash.Compute(request);

            Assert.Equal(first, second);
            Assert.NotEqual(first, GovernedLoopRevisionLifecycleRequestHash.Compute(request with { OperationId = "operation-2" }));
            Assert.NotEqual(first, GovernedLoopRevisionLifecycleRequestHash.Compute(request with { CandidateRevision = Revision("graph-a", "revision-2", '1') }));
            Assert.Matches("^[0-9a-f]{64}$", first);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        var publicJson = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("RequestHash", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorized", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorityEvidence", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Credential", publicJson, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentNullException>(() => GovernedLoopRevisionLifecycleRequestHash.Compute(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionLifecycleRequestHash.Compute(request with { OperationId = "bad\ud800" }));
    }

    [Fact]
    public void Request_validator_rejects_every_malformed_lifecycle_and_operation_shape_without_throwing()
    {
        var first = Revision("graph-a", "revision-1", '1');
        var second = Revision("graph-a", "revision-2", '2');
        var publication = new GovernedLoopRevisionPublicationPin(1, first, "publish-1", Hash('a'));
        var draft = HeadExpectation("graph-a", 1, GovernedLoopRevisionLifecycleStatus.Draft, first, null);
        var published = HeadExpectation("graph-a", 2, GovernedLoopRevisionLifecycleStatus.Published, null, publication);
        var create = Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first);
        var malformed = new GovernedLoopRevisionLifecycleRequest?[]
        {
            create with { SchemaVersion = 2 },
            create with { OperationId = "BAD ID" },
            create with { GraphId = "BAD ID" },
            create with { ActorId = null! },
            create with { Kind = GovernedLoopRevisionOperationKind.Unknown },
            create with { ExpectedLifecycleVersion = -1 },
            create with { ExpectedLifecycleVersion = 1, ExpectedLifecycleStatus = GovernedLoopRevisionLifecycleStatus.Draft, ExpectedDraftRevision = first },
            create with { ExpectedLifecycleVersion = 1, ExpectedLifecycleStatus = GovernedLoopRevisionLifecycleStatus.Unknown },
            create with { CandidateRevision = null },
            create with { TargetRevision = first },
            create with { RollbackSourcePublication = publication },
            Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "replace-bad", draft, target: first),
            Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "replace-bad-2", draft, candidate: second),
            Request(GovernedLoopRevisionOperationKind.Publish, "publish-bad", draft, candidate: second),
            Request(GovernedLoopRevisionOperationKind.Publish, "publish-bad-2", draft),
            Request(GovernedLoopRevisionOperationKind.Disable, "disable-bad", published, candidate: second, target: first),
            Request(GovernedLoopRevisionOperationKind.Archive, "archive-bad", draft, target: first),
            Request(GovernedLoopRevisionOperationKind.Rollback, "rollback-bad", published, candidate: second, target: first),
            Request(
                GovernedLoopRevisionOperationKind.Rollback,
                "rollback-hash-bad",
                published,
                candidate: second,
                target: first,
                rollbackSource: new GovernedLoopRevisionPublicationPin(1, Revision("graph-a", "source-1", '3'), "publish-source", Hash('3'))),
            Request(GovernedLoopRevisionOperationKind.Publish, "publish-graph-bad", draft, target: Revision("graph-b", "revision-1", '1')),
            new GovernedLoopRevisionLifecycleRequest(
                1,
                "archive-draft-bad",
                GovernedLoopRevisionOperationKind.Archive,
                "graph-a",
                create.ActorId,
                GovernedLoopRevisionLifecycleStatus.Archived,
                3,
                second,
                publication,
                null,
                first,
                null),
            create with
            {
                CandidateRevision = Revision("graph-b", "revision-1", '1'),
            },
            create with
            {
                RollbackSourcePublication = new GovernedLoopRevisionPublicationPin(2, first, "BAD ID", "not-a-hash"),
            },
        };

        foreach (var candidate in malformed)
        {
            var errors = GovernedLoopRevisionLifecycleRequestValidator.Validate(candidate);
            Assert.NotEmpty(errors);
            Assert.InRange(errors.Count, 1, GovernedLoopRevisionContractLimits.MaxValidationErrors);
            Assert.All(errors, error => Assert.InRange(error.Path.Length, 1, GovernedLoopRevisionContractLimits.MaxErrorPathCharacters));
        }
    }

    [Fact]
    public async Task Unauthorized_and_hostile_authority_results_fail_closed_after_bounded_replay_recovery()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "operation-denied",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var store = new InMemoryStore();
        var denied = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };

        var deniedResult = await Service(store, denied).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unauthorized, deniedResult.Status);
        Assert.Equal(1, store.MutationReads);
        Assert.Equal(0, store.Commits);

        var hostile = new StubAuthorizer { CorruptRequestHash = true };
        var hostileResult = await Service(store, hostile).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, hostileResult.Status);
        Assert.Equal(2, store.MutationReads);

        hostile = new StubAuthorizer { Throw = true };
        hostileResult = await Service(store, hostile).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, hostileResult.Status);
        Assert.Equal(3, store.MutationReads);
    }

    [Fact]
    public async Task Null_unknown_mismatched_and_unavailable_authority_decisions_fail_closed()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "authority-shapes",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var authorizers = new[]
        {
            new StubAuthorizer { ReturnNull = true },
            new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Unknown },
            new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Unavailable },
            new StubAuthorizer { CorruptOperationId = true },
            new StubAuthorizer { CorruptActor = true },
            new StubAuthorizer { InvalidEvidenceHash = true },
        };

        foreach (var authorizer in authorizers)
        {
            var store = new InMemoryStore();
            var result = await Service(store, authorizer).MutateAsync(request);
            Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, result.Status);
            Assert.Equal(1, store.MutationReads);
        }
    }

    [Fact]
    public async Task Invalid_or_throwing_server_clock_allows_only_bounded_replay_recovery()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "clock-unavailable",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        foreach (var timeProvider in new TimeProvider[] { new TestTimeProvider(default), new ThrowingTimeProvider() })
        {
            var store = new InMemoryStore();
            var authorizer = new StubAuthorizer();
            var result = await Service(store, authorizer, timeProvider: timeProvider).MutateAsync(request);
            Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, result.Status);
            Assert.Equal(0, authorizer.Calls);
            Assert.Equal(1, store.MutationReads);
        }
    }

    [Fact]
    public async Task Exact_terminal_replay_precedes_revoked_unavailable_and_clock_dependent_authority()
    {
        var store = new InMemoryStore();
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "replay-before-authority",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var committed = await Service(store).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, committed.Status);

        var revoked = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var revokedReplay = await Service(store, revoked).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, revokedReplay.Status);
        Assert.Equal(committed.Evidence, revokedReplay.Evidence);
        Assert.Equal(0, revoked.Calls);

        var unavailable = new StubAuthorizer { Throw = true };
        var unavailableReplay = await Service(store, unavailable).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, unavailableReplay.Status);
        Assert.Equal(committed.Evidence, unavailableReplay.Evidence);
        Assert.Equal(0, unavailable.Calls);

        var clockReplay = await Service(
            store,
            new StubAuthorizer { Throw = true },
            timeProvider: new ThrowingTimeProvider()).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, clockReplay.Status);
        Assert.Equal(committed.Evidence, clockReplay.Evidence);

        var ambiguousStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.Ambiguous,
                0,
                null,
                null),
        };
        var revokedDuringRecovery = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var ambiguousRecovery = await Service(ambiguousStore, revokedDuringRecovery).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, ambiguousRecovery.Status);
        Assert.Equal(0, revokedDuringRecovery.Calls);
    }

    [Fact]
    public async Task Changed_operation_intent_never_bypasses_current_authority()
    {
        var store = new InMemoryStore();
        var original = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "changed-intent",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, (await Service(store).MutateAsync(original)).Status);
        var changed = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            original.OperationId,
            null,
            candidate: Revision("graph-a", "revision-2", '2'));

        var denied = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var deniedResult = await Service(store, denied).MutateAsync(changed);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unauthorized, deniedResult.Status);
        Assert.Equal(1, denied.Calls);

        var unavailable = new StubAuthorizer { Throw = true };
        var unavailableResult = await Service(store, unavailable).MutateAsync(changed);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, unavailableResult.Status);
        Assert.Equal(1, unavailable.Calls);
    }

    [Fact]
    public async Task Missing_lifecycle_receipt_replays_but_hostile_copied_hash_bindings_are_ambiguous()
    {
        var sourceRevision = Revision("graph-a", "revision-1", '1');
        var missingPublication = new GovernedLoopRevisionPublicationPin(1, sourceRevision, "publish-never", Hash('a'));
        var expected = HeadExpectation("graph-a", 1, GovernedLoopRevisionLifecycleStatus.Published, null, missingPublication);
        var request = Request(
            GovernedLoopRevisionOperationKind.Disable,
            "missing-disable",
            expected,
            target: sourceRevision);
        var receiptStore = new InMemoryStore();
        var receipt = await Service(receiptStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.NotFound, receipt.Status);
        var receiptEvidence = Assert.IsType<GovernedLoopRevisionOperationEvidence>(receipt.Evidence);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.LifecycleNotFound, receiptEvidence.FailureCode);
        Assert.Null(receiptEvidence.PreviousHead);
        Assert.Null(receiptEvidence.ResultHead);

        var revoked = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var replay = await Service(receiptStore, revoked).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(receiptEvidence, replay.Evidence);
        Assert.Equal(0, revoked.Calls);

        var zeroGenerationStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.NotFound,
                0,
                null,
                new GovernedLoopRevisionStoredOperation(request.GraphId, receiptEvidence)),
        };
        var zeroGenerationAuthorizer = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var zeroGenerationReplay = await Service(zeroGenerationStore, zeroGenerationAuthorizer).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, zeroGenerationReplay.Status);
        Assert.Equal(0, zeroGenerationAuthorizer.Calls);

        var changedHashMalformedEvidence = receiptEvidence with
        {
            RequestHash = Hash('f'),
            FailureCode = GovernedLoopRevisionOperationFailureCode.RevisionNotFound,
        };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(changedHashMalformedEvidence).IsValid);
        var changedHashMalformedStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.NotFound,
                1,
                null,
                new GovernedLoopRevisionStoredOperation(request.GraphId, changedHashMalformedEvidence)),
        };
        var changedHashMalformedAuthorizer = new StubAuthorizer();
        var changedHashMalformed = await Service(changedHashMalformedStore, changedHashMalformedAuthorizer).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, changedHashMalformed.Status);
        Assert.Equal(0, changedHashMalformedAuthorizer.Calls);
        Assert.Equal(0, changedHashMalformedStore.Commits);

        var changedHashAbsentEvidence = receiptEvidence with { RequestHash = Hash('e') };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(changedHashAbsentEvidence).IsValid);
        var changedHashAbsentStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.NotFound,
                1,
                null,
                new GovernedLoopRevisionStoredOperation(request.GraphId, changedHashAbsentEvidence)),
        };
        var changedHashAbsentAuthorizer = new StubAuthorizer();
        var changedHashAbsent = await Service(changedHashAbsentStore, changedHashAbsentAuthorizer).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, changedHashAbsent.Status);
        Assert.Equal(1, changedHashAbsentAuthorizer.Calls);
        Assert.Equal(0, changedHashAbsentStore.Commits);

        var hostileEvidence = new[]
        {
            receiptEvidence with { ActorId = "actor-foreign" },
            receiptEvidence with { Kind = GovernedLoopRevisionOperationKind.Archive },
            receiptEvidence with { TargetRevision = Revision("graph-a", "revision-foreign", 'f') },
            receiptEvidence with { FailureCode = GovernedLoopRevisionOperationFailureCode.RevisionNotFound },
        };
        foreach (var evidence in hostileEvidence)
        {
            Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
            var hostileStore = new InMemoryStore
            {
                MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                    GovernedLoopRevisionStoreReadStatus.NotFound,
                    1,
                    null,
                    new GovernedLoopRevisionStoredOperation(request.GraphId, evidence)),
            };
            var authorizer = new StubAuthorizer();
            var hostileResult = await Service(hostileStore, authorizer).MutateAsync(request);
            Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, hostileResult.Status);
            Assert.Null(hostileResult.Evidence);
            Assert.Equal(
                evidence.FailureCode == GovernedLoopRevisionOperationFailureCode.LifecycleNotFound ? 1 : 0,
                authorizer.Calls);
            Assert.Equal(0, hostileStore.Commits);
        }
    }

    [Fact]
    public async Task Not_found_store_state_cannot_replay_an_exact_committed_graph_operation()
    {
        var store = new InMemoryStore();
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "impossible-not-found-replay",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var committed = await Service(store).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionOperationOutcome.Committed, committed.Evidence!.Outcome);
        var commitsBeforeHostileRead = store.Commits;
        store.MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.NotFound,
            store.StoreGeneration,
            null,
            new GovernedLoopRevisionStoredOperation(request.GraphId, committed.Evidence));

        var result = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(commitsBeforeHostileRead, store.Commits);
    }

    [Fact]
    public async Task Regressed_trusted_clock_is_unavailable_before_commit_intent()
    {
        var store = new InMemoryStore();
        var current = Revision("graph-a", "revision-1", '1');
        var created = await Service(store).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "clock-seed",
            null,
            candidate: current));
        var commitsBeforeRegression = store.Commits;

        var result = await Service(store, timeProvider: new TestTimeProvider(_now.AddTicks(-1))).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "clock-regressed",
            created.Head,
            candidate: Revision("graph-a", "revision-2", '2'),
            target: current));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, result.Status);
        Assert.Equal(created.Head, result.Head);
        Assert.Equal(commitsBeforeRegression, store.Commits);
    }

    [Fact]
    public async Task Publication_validation_and_durable_evidence_use_the_post_authorization_instant()
    {
        var timeProvider = new TestTimeProvider(_now);
        var authorizer = new StubAuthorizer
        {
            OnCall = _ => timeProvider.Advance(TimeSpan.FromSeconds(1)),
        };
        var validator = new StubPublishValidator();
        var store = new InMemoryStore();
        var service = Service(store, authorizer, validator, timeProvider: timeProvider);
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "post-authority-create",
            null,
            candidate: revision));

        var published = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Publish,
            "post-authority-publish",
            created.Head,
            target: revision));

        Assert.Equal(4, authorizer.Calls);
        Assert.Equal(_now.AddSeconds(3), authorizer.Requests[^1].EvaluatedAtUtc);
        Assert.Equal(_now.AddSeconds(4), validator.LastRequest!.EvaluatedAtUtc);
        Assert.Equal(_now.AddSeconds(4), published.Evidence!.RecordedAtUtc);
        Assert.Equal(_now.AddSeconds(4), published.Head!.UpdatedAtUtc);
        Assert.True(published.Evidence.RecordedAtUtc > authorizer.Requests[^1].EvaluatedAtUtc);
    }

    [Fact]
    public async Task Create_draft_commits_exact_server_owned_evidence_under_one_transaction()
    {
        var store = new InMemoryStore();
        var transaction = new StubAuthorityTransaction();
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "create-draft",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var result = await Service(store, transaction: transaction).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(1, transaction.Executions);
        Assert.Equal(2, store.MutationReads);
        Assert.Equal(1, store.Commits);
        Assert.All(store.ReadRequestHashes, hash => Assert.Equal(result.RequestHash, hash));
        Assert.NotNull(result.Evidence);
        Assert.True(GovernedLoopRevisionContractValidator.Validate(result.Evidence).IsValid);
        Assert.Equal(_authorityHash, result.Evidence!.AuthorityEvidenceHash);
        Assert.Null(result.Evidence.PublicationValidationEvidenceHash);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Draft, result.Head!.Status);
        Assert.Equal(request.CandidateRevision, result.Head.DraftRevision);
        Assert.Null(result.Head.PublishedRevision);
        Assert.Equal(_now, result.Head.UpdatedAtUtc);
        Assert.Equal(request.ActorId.Value, store.LastMutation!.ArtifactToAppend!.CreatedByActorId);
        Assert.Null(store.LastMutation.ArtifactToAppend.PredecessorRevision);
    }

    [Fact]
    public async Task Create_draft_accepts_the_full_authority_actor_id_contract_without_ambiguous_evidence()
    {
        var actorIds = new[] { "con", new string('a', AuthorityContractLimits.MaxActorIdCharacters) };

        foreach (var actorId in actorIds)
        {
            Assert.True(AuthorityActorId.TryParse(actorId, out var actor, out _));
            var store = new InMemoryStore();
            var request = Request(
                GovernedLoopRevisionOperationKind.CreateDraft,
                "create-authority-actor",
                null,
                candidate: Revision("graph-a", "revision-1", '1')) with
            {
                ActorId = actor!,
            };

            var result = await Service(store).MutateAsync(request);

            Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, result.Status);
            Assert.Equal(actorId, result.Evidence!.ActorId);
            Assert.Equal(actorId, store.LastMutation!.ArtifactToAppend!.CreatedByActorId);
        }
    }

    [Fact]
    public async Task Exact_operation_replays_and_changed_intent_conflicts_workspace_globally()
    {
        var store = new InMemoryStore();
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "global-operation",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var service = Service(store);
        var committed = await service.MutateAsync(request);

        var replay = await service.MutateAsync(request);
        var changed = await service.MutateAsync(request with { GraphId = "graph-b", CandidateRevision = Revision("graph-b", "revision-1", '1') });

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(committed.Evidence, replay.Evidence);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, changed.Status);
        Assert.Null(changed.Evidence);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task Published_graph_can_append_a_successor_draft_then_publish_an_exact_new_pin()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var first = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first));
        var published = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-1", created.Head, target: first));
        var firstPin = published.Head!.PublishedRevision;
        var second = Revision("graph-a", "revision-2", '2');

        var drafted = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "draft-2", published.Head, candidate: second, target: first));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, drafted.Status);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, drafted.Head!.Status);
        Assert.Equal(second, drafted.Head.DraftRevision);
        Assert.Equal(firstPin, drafted.Head.PublishedRevision);
        Assert.Equal(first, store.LastMutation!.ArtifactToAppend!.PredecessorRevision);

        var republished = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-2", drafted.Head, target: second));
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, republished.Head!.Status);
        Assert.Null(republished.Head.DraftRevision);
        Assert.Equal(second, republished.Head.PublishedRevision!.Revision);
        Assert.Equal("publish-2", republished.Head.PublishedRevision.PublicationOperationId);
        Assert.Equal(_validationHash, republished.Head.PublishedRevision.ValidationEvidenceHash);
        Assert.Equal(_validationHash, republished.Evidence!.PublicationValidationEvidenceHash);
    }

    [Fact]
    public async Task Disable_archive_and_exact_pin_resolution_preserve_history_and_terminal_posture()
    {
        var store = new InMemoryStore();
        var transaction = new StubAuthorityTransaction();
        var service = Service(store, transaction: transaction);
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: revision));
        var published = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-1", created.Head, target: revision));
        var pin = published.Head!.PublishedRevision!;
        var source = new GovernedLoopPublishedRevisionSource(store, transaction);

        var active = await source.ResolveAsync(pin);
        var disabled = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Disable, "disable-1", published.Head, target: revision));
        var disabledResolution = await source.ResolveAsync(pin);
        var archived = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Archive, "archive-1", disabled.Head, target: revision));
        var archivedResolution = await source.ResolveAsync(pin);
        var terminal = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Disable, "disable-after-archive", archived.Head, target: revision));

        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Active, active.Status);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Disabled, disabledResolution.Status);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Archived, archivedResolution.Status);
        Assert.Equal(pin, archivedResolution.RequestedPin);
        Assert.Equal(revision, archivedResolution.Artifact!.Revision);
        Assert.Equal(archived.Head!.LifecycleVersion, archivedResolution.ObservedLifecycleVersion);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, terminal.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.LifecycleArchived, terminal.Evidence!.FailureCode);
        Assert.Null(archived.Head.DraftRevision);
        Assert.Equal(pin, archived.Head.PublishedRevision);
    }

    [Fact]
    public async Task Rollback_cannot_reuse_its_source_publication_operation_identifier()
    {
        var sourceRevision = Revision("graph-a", "revision-1", '1');
        var source = new GovernedLoopRevisionPublicationPin(1, sourceRevision, "rollback-self", Hash('a'));
        var expected = HeadExpectation("graph-a", 1, GovernedLoopRevisionLifecycleStatus.Published, null, source);
        var request = Request(
            GovernedLoopRevisionOperationKind.Rollback,
            source.PublicationOperationId,
            expected,
            candidate: Revision("graph-a", "revision-2", '1'),
            target: sourceRevision,
            rollbackSource: source);

        var validationErrors = GovernedLoopRevisionLifecycleRequestValidator.Validate(request);
        Assert.Contains(validationErrors, error => error.Code == GovernedLoopRevisionLifecycleValidationErrorCode.InvalidReference
            && error.Path == "rollbackSourcePublication.publicationOperationId");

        var store = new InMemoryStore();
        var authorizer = new StubAuthorizer();
        var result = await Service(store, authorizer).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Invalid, result.Status);
        Assert.Equal(0, authorizer.Calls);
        Assert.Equal(0, store.MutationReads);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Rollback_publishes_a_distinct_successor_from_proved_history_and_uses_current_draft_as_predecessor()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var first = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first));
        var firstPublished = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-1", created.Head, target: first));
        var source = firstPublished.Head!.PublishedRevision!;
        var second = Revision("graph-a", "revision-2", '2');
        var drafted = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "draft-2", firstPublished.Head, candidate: second, target: first));
        var secondPublished = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-2", drafted.Head, target: second));
        var abandonedDraft = Revision("graph-a", "revision-3", '3');
        var withDraft = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "draft-3", secondPublished.Head, candidate: abandonedDraft, target: second));
        var rollback = Revision("graph-a", "revision-4", '1');

        var result = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Rollback,
            "rollback-1",
            withDraft.Head,
            candidate: rollback,
            target: abandonedDraft,
            rollbackSource: source));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(rollback, result.Head!.PublishedRevision!.Revision);
        Assert.Null(result.Head.DraftRevision);
        Assert.Equal(source, result.Evidence!.RollbackSourcePublication);
        Assert.Equal(abandonedDraft, result.Evidence.TargetRevision);
        Assert.Equal(abandonedDraft, store.LastMutation!.ArtifactToAppend!.PredecessorRevision);
        Assert.Equal(source, store.LastMutation.ArtifactToAppend.RollbackSourcePublication);
        Assert.Equal(first.ExecutableHash, rollback.ExecutableHash);

        var oldPinResolution = await new GovernedLoopPublishedRevisionSource(store, new StubAuthorityTransaction()).ResolveAsync(source);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Stale, oldPinResolution.Status);
    }

    [Fact]
    public async Task Missing_rollback_publication_is_a_durable_replayable_receipt_with_requested_provenance()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var current = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: current));
        var published = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-1", created.Head, target: current));
        var missingSource = new GovernedLoopRevisionPublicationPin(
            1,
            Revision("graph-a", "revision-missing", '9'),
            "publish-missing",
            Hash('9'));
        var request = Request(
            GovernedLoopRevisionOperationKind.Rollback,
            "rollback-missing",
            published.Head,
            candidate: Revision("graph-a", "revision-2", '9'),
            target: current,
            rollbackSource: missingSource);

        var missing = await service.MutateAsync(request);
        var replay = await service.MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.NotFound, missing.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.PublicationNotFound, missing.Evidence!.FailureCode);
        Assert.Equal(missingSource, missing.Evidence.RollbackSourcePublication);
        Assert.Null(missing.Evidence.PublicationValidationEvidenceHash);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(missing.Evidence, replay.Evidence);
    }

    [Fact]
    public async Task Rollback_source_publication_must_precede_the_rollback_in_append_order()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var sourceRevision = Revision("graph-a", "revision-source", '1');
        var created = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "future-create-source",
            null,
            candidate: sourceRevision));
        var sourcePublished = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Publish,
            "future-publish-source",
            created.Head,
            target: sourceRevision));
        var abandonedDraft = Revision("graph-a", "revision-abandoned", '2');
        var drafted = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "future-create-abandoned",
            sourcePublished.Head,
            candidate: abandonedDraft,
            target: sourceRevision));
        var rollbackRevision = Revision("graph-a", "revision-rollback", '1');
        var rolledBack = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Rollback,
            "future-rollback",
            drafted.Head,
            candidate: rollbackRevision,
            target: abandonedDraft,
            rollbackSource: sourcePublished.Head!.PublishedRevision));
        var futureRevision = Revision("graph-a", "revision-future", '1');
        var futureDrafted = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "future-create-draft",
            rolledBack.Head,
            candidate: futureRevision,
            target: rollbackRevision));
        var futurePublished = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Publish,
            "future-publish-draft",
            futureDrafted.Head,
            target: futureRevision));
        var futurePin = futurePublished.Head!.PublishedRevision!;
        var read = await store.ReadForMutationAsync("graph-a", "future-read", Hash('e'));
        var exactSnapshot = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(read.Snapshot);
        var hostileSnapshot = new GovernedLoopRevisionStoreSnapshot(
            exactSnapshot.Head,
            Array.AsReadOnly(exactSnapshot.Artifacts
                .Select(artifact => artifact.CreationOperationId == "future-rollback"
                    ? artifact with { RollbackSourcePublication = futurePin }
                    : artifact)
                .ToArray()),
            Array.AsReadOnly(exactSnapshot.Operations
                .Select(operation => operation.OperationId == "future-rollback"
                    ? operation with { RollbackSourcePublication = futurePin }
                    : operation)
                .ToArray()));
        Assert.All(hostileSnapshot.Artifacts, artifact => Assert.True(GovernedLoopRevisionContractValidator.Validate(artifact).IsValid));
        Assert.All(hostileSnapshot.Operations, operation => Assert.True(GovernedLoopRevisionContractValidator.Validate(operation).IsValid));

        var hostileStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                store.StoreGeneration,
                hostileSnapshot,
                null),
        };
        var result = await Service(hostileStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Archive,
            "future-proof-probe",
            futurePublished.Head,
            target: futureRevision));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Equal(0, hostileStore.Commits);
    }

    [Fact]
    public async Task Publication_rejection_or_hostile_validation_never_publishes_durable_intent()
    {
        var store = new InMemoryStore();
        var invalidValidator = new StubPublishValidator { Status = GovernedLoopRevisionPublishValidationStatus.Invalid };
        var service = Service(store, validator: invalidValidator);
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: revision));
        var commitsBeforePublish = store.Commits;

        var rejected = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-invalid", created.Head, target: revision));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.PublicationRejected, rejected.Status);
        Assert.Equal(commitsBeforePublish, store.Commits);

        var hostile = new StubPublishValidator { CorruptRevision = true };
        var unavailable = await Service(store, validator: hostile).MutateAsync(Request(GovernedLoopRevisionOperationKind.Publish, "publish-hostile", created.Head, target: revision));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, unavailable.Status);
        Assert.Equal(commitsBeforePublish, store.Commits);
    }

    [Fact]
    public async Task Null_unknown_unavailable_throwing_and_mismatched_publication_decisions_fail_closed()
    {
        var store = new InMemoryStore();
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await Service(store).MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: revision));
        var validators = new[]
        {
            new StubPublishValidator { ReturnNull = true },
            new StubPublishValidator { Status = GovernedLoopRevisionPublishValidationStatus.Unknown },
            new StubPublishValidator { Status = GovernedLoopRevisionPublishValidationStatus.Unavailable },
            new StubPublishValidator { CorruptOperationId = true },
            new StubPublishValidator { CorruptRequestHash = true },
            new StubPublishValidator { InvalidEvidenceHash = true },
            new StubPublishValidator { Throw = true },
        };

        var index = 0;
        foreach (var validator in validators)
        {
            var result = await Service(store, validator: validator).MutateAsync(Request(
                GovernedLoopRevisionOperationKind.Publish,
                $"publish-shape-{index++}",
                created.Head,
                target: revision));
            Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, result.Status);
        }

        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task Not_found_and_optimistic_conflict_are_durable_exact_receipts()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var missingRevision = Revision("graph-a", "revision-1", '1');
        var missingRequest = Request(
            GovernedLoopRevisionOperationKind.Publish,
            "publish-missing",
            HeadExpectation("graph-a", 1, GovernedLoopRevisionLifecycleStatus.Draft, missingRevision, null),
            target: missingRevision);

        var missing = await service.MutateAsync(missingRequest);
        var missingReplay = await service.MutateAsync(missingRequest);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.NotFound, missing.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.LifecycleNotFound, missing.Evidence!.FailureCode);
        Assert.Null(missing.Evidence.PublicationValidationEvidenceHash);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, missingReplay.Status);

        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: missingRevision));
        var stale = created.Head! with { LifecycleVersion = created.Head.LifecycleVersion + 1 };
        var conflictRequest = Request(GovernedLoopRevisionOperationKind.Publish, "publish-stale", stale, target: missingRevision);
        var conflict = await service.MutateAsync(conflictRequest);
        var conflictReplay = await service.MutateAsync(conflictRequest);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, conflict.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict, conflict.Evidence!.FailureCode);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, conflictReplay.Status);
    }

    [Fact]
    public async Task Final_authority_revalidation_can_deny_without_committing()
    {
        var store = new InMemoryStore();
        var authorizer = new StubAuthorizer();
        authorizer.Statuses.Enqueue(GovernedLoopRevisionActorAuthorizationStatus.Authorized);
        authorizer.Statuses.Enqueue(GovernedLoopRevisionActorAuthorizationStatus.Denied);
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "authority-changed",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var result = await Service(store, authorizer).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unauthorized, result.Status);
        Assert.Equal(2, authorizer.Calls);
        Assert.Equal(2, store.MutationReads);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Store_generation_conflict_is_reread_and_retried_with_the_same_canonical_hash()
    {
        var store = new InMemoryStore { ForcedStoreConflicts = 1 };
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "retry-conflict",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var result = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(2, store.Commits);
        Assert.Equal(3, store.MutationReads);
        Assert.Single(store.ReadRequestHashes.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Explicit_pre_intent_store_unavailability_may_return_a_valid_current_snapshot()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var first = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first));
        var snapshot = store.Snapshot("graph-a")!;
        store.CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
            GovernedLoopRevisionStoreCommitStatus.Unavailable,
            store.StoreGeneration,
            null,
            snapshot);

        var result = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "quota-unavailable",
            created.Head,
            candidate: Revision("graph-a", "revision-2", '2'),
            target: first));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, result.Status);
        Assert.Equal(created.Head, result.Head);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData(GovernedLoopRevisionStoreReadStatus.Unavailable, GovernedLoopRevisionLifecycleMutationStatus.Unavailable)]
    [InlineData(GovernedLoopRevisionStoreReadStatus.Ambiguous, GovernedLoopRevisionLifecycleMutationStatus.Ambiguous)]
    [InlineData(GovernedLoopRevisionStoreReadStatus.Unknown, GovernedLoopRevisionLifecycleMutationStatus.Ambiguous)]
    [InlineData(GovernedLoopRevisionStoreReadStatus.Ready, GovernedLoopRevisionLifecycleMutationStatus.Ambiguous)]
    public async Task Hostile_or_nonready_mutation_reads_map_to_closed_fail_safe_results(
        GovernedLoopRevisionStoreReadStatus storeStatus,
        GovernedLoopRevisionLifecycleMutationStatus expectedStatus)
    {
        var store = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(storeStatus, 0, null, null),
        };
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            $"read-{storeStatus.ToString().ToLowerInvariant()}",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var result = await Service(store).MutateAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Null_throwing_and_smuggled_mutation_reads_fail_closed()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "read-hostile",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var nullStore = new InMemoryStore { MutationReadOverride = (_, _, _) => null! };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(nullStore).MutateAsync(request)).Status);

        var throwingStore = new InMemoryStore { MutationReadException = new IOException("read unavailable") };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
            (await Service(throwingStore).MutateAsync(request)).Status);

        var smuggled = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.NotFound,
                1,
                new GovernedLoopRevisionStoreSnapshot(null!, null!, null!),
                null),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(smuggled).MutateAsync(request)).Status);
    }

    [Fact]
    public async Task Commit_result_variants_require_exact_durable_evidence_and_head()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "commit-shape",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var nullStore = new InMemoryStore { CommitOverride = _ => null! };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(nullStore).MutateAsync(request)).Status);

        var exactAmbiguousStore = new InMemoryStore
        {
            CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Ambiguous, mutation),
        };
        var exactAmbiguous = await Service(exactAmbiguousStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactAmbiguous.Status);
        Assert.NotNull(exactAmbiguous.Evidence);

        var unrelatedStore = new InMemoryStore();
        _ = await Service(unrelatedStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "unrelated-ambiguous-create",
            null,
            candidate: Revision("graph-a", "unrelated-revision", '2')));
        var inconsistentAmbiguousStore = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Ambiguous,
                mutation.ExpectedStoreGeneration + 1,
                new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation),
                unrelatedStore.Snapshot(mutation.GraphId)),
        };
        var inconsistentAmbiguous = await Service(inconsistentAmbiguousStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, inconsistentAmbiguous.Status);
        Assert.Null(inconsistentAmbiguous.Evidence);
        Assert.Null(inconsistentAmbiguous.Head);

        var replayStore = new InMemoryStore
        {
            CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Replayed, mutation),
        };
        var replay = await Service(replayStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(request.OperationId, replay.Evidence!.OperationId);

        var contradictoryConflictStore = new InMemoryStore
        {
            CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.OperationConflict, mutation),
        };
        var contradictoryConflict = await Service(contradictoryConflictStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, contradictoryConflict.Status);
        Assert.Null(contradictoryConflict.Evidence);

        var unprovedConflictStore = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                mutation.ExpectedStoreGeneration,
                null,
                null),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(unprovedConflictStore).MutateAsync(request)).Status);

        var wrongOperationConflictStore = new InMemoryStore
        {
            CommitOverride = mutation =>
            {
                var wrongHead = mutation.Operation.ResultHead! with { LastOperationId = "wrong-operation" };
                var wrongOperation = mutation.Operation with
                {
                    OperationId = "wrong-operation",
                    ResultHead = wrongHead,
                };
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                    mutation.ExpectedStoreGeneration,
                    new GovernedLoopRevisionStoredOperation(mutation.GraphId, wrongOperation),
                    null);
            },
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(wrongOperationConflictStore).MutateAsync(request)).Status);

        var unknownStore = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Unknown,
                mutation.ExpectedStoreGeneration,
                null,
                null),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(unknownStore).MutateAsync(request)).Status);

        var wrongHeadStore = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation),
                null),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(wrongHeadStore).MutateAsync(request)).Status);
    }

    [Fact]
    public async Task Store_conflict_retry_requires_a_changed_generation_without_smuggled_state()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "hostile-store-conflict",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var unchangedGeneration = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                mutation.ExpectedStoreGeneration,
                null,
                null),
        };
        var unchangedResult = await Service(unchangedGeneration).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, unchangedResult.Status);
        Assert.Equal(1, unchangedGeneration.Commits);

        var smuggledOperation = new InMemoryStore
        {
            CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                mutation.ExpectedStoreGeneration + 1,
                new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation),
                null),
        };
        var smuggledResult = await Service(smuggledOperation).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, smuggledResult.Status);
        Assert.Equal(1, smuggledOperation.Commits);

        var seededStore = new InMemoryStore();
        var current = Revision("graph-a", "revision-current", '3');
        var created = await Service(seededStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "conflict-generation-seed",
            null,
            candidate: current));
        seededStore.CommitOverride = mutation => new GovernedLoopRevisionStoreCommitResult(
            GovernedLoopRevisionStoreCommitStatus.StoreConflict,
            mutation.ExpectedStoreGeneration - 1,
            null,
            null);
        var regressed = await Service(seededStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "hostile-regressed-conflict",
            created.Head,
            candidate: Revision("graph-a", "revision-next", '4'),
            target: current));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, regressed.Status);
    }

    [Fact]
    public async Task Commit_and_replay_generations_must_prove_the_atomic_successor_or_intervening_commit()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "commit-generation-shapes",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        foreach (var generationOffset in new long[] { 0, 2 })
        {
            var committedStore = new InMemoryStore
            {
                CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Committed, mutation) with
                {
                    StoreGeneration = mutation.ExpectedStoreGeneration + generationOffset,
                },
            };
            Assert.Equal(
                GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                (await Service(committedStore).MutateAsync(request)).Status);
        }

        var replayStore = new InMemoryStore
        {
            CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Replayed, mutation) with
            {
                StoreGeneration = mutation.ExpectedStoreGeneration,
            },
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(replayStore).MutateAsync(request)).Status);

        var operationConflictStore = new InMemoryStore
        {
            CommitOverride = mutation =>
            {
                var foreignCandidate = Revision("graph-foreign", "revision-1", '1');
                var foreignHead = mutation.Operation.ResultHead! with
                {
                    GraphId = foreignCandidate.GraphId,
                    DraftRevision = foreignCandidate,
                };
                var foreignEvidence = mutation.Operation with
                {
                    CandidateRevision = foreignCandidate,
                    ResultHead = foreignHead,
                };
                Assert.True(GovernedLoopRevisionContractValidator.Validate(foreignEvidence).IsValid);
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                    mutation.ExpectedStoreGeneration,
                    new GovernedLoopRevisionStoredOperation(foreignCandidate.GraphId, foreignEvidence),
                    null);
            },
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(operationConflictStore).MutateAsync(request)).Status);
    }

    [Fact]
    public async Task Operation_conflict_requires_causal_proof_for_the_returned_global_operation()
    {
        const string SharedOperationId = "causal-shared-operation";
        var sameGraphProofStore = new InMemoryStore();
        var sameGraphOriginal = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            SharedOperationId,
            null,
            candidate: Revision("graph-a", "revision-original", '1'));
        var sameGraphCommitted = await Service(sameGraphProofStore).MutateAsync(sameGraphOriginal);
        var sameGraphOperation = new GovernedLoopRevisionStoredOperation(
            sameGraphOriginal.GraphId,
            Assert.IsType<GovernedLoopRevisionOperationEvidence>(sameGraphCommitted.Evidence));
        var includedSnapshot = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(sameGraphProofStore.Snapshot("graph-a"));
        var changedRequest = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            SharedOperationId,
            null,
            candidate: Revision("graph-a", "revision-changed", '2'));

        var includedStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                1,
                sameGraphOperation,
                includedSnapshot),
        };
        var included = await Service(includedStore).MutateAsync(changedRequest);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, included.Status);
        Assert.Equal(1, includedStore.Commits);

        var otherProofStore = new InMemoryStore();
        await Service(otherProofStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "causal-other-operation",
            null,
            candidate: Revision("graph-a", "revision-other", '3')));
        var missingEvidenceSnapshot = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(otherProofStore.Snapshot("graph-a"));
        var missingEvidenceStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                1,
                sameGraphOperation,
                missingEvidenceSnapshot),
        };
        var missingEvidence = await Service(missingEvidenceStore).MutateAsync(changedRequest);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, missingEvidence.Status);
        Assert.Equal(1, missingEvidenceStore.Commits);

        var nullNonAbsenceStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                1,
                sameGraphOperation,
                null),
        };
        var nullNonAbsence = await Service(nullNonAbsenceStore).MutateAsync(changedRequest);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, nullNonAbsence.Status);

        var absentEvidence = sameGraphOperation.Evidence with
        {
            Outcome = GovernedLoopRevisionOperationOutcome.NotFound,
            FailureCode = GovernedLoopRevisionOperationFailureCode.LifecycleNotFound,
            PreviousHead = null,
            ResultHead = null,
        };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(absentEvidence).IsValid);
        var absentReceiptStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                1,
                new GovernedLoopRevisionStoredOperation("graph-a", absentEvidence),
                null),
        };
        var absentReceipt = await Service(absentReceiptStore).MutateAsync(changedRequest);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, absentReceipt.Status);

        var foreignProofStore = new InMemoryStore();
        var foreignRequest = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            SharedOperationId,
            null,
            candidate: Revision("graph-foreign", "revision-foreign", 'f'));
        var foreignCommitted = await Service(foreignProofStore).MutateAsync(foreignRequest);
        var foreignOperation = new GovernedLoopRevisionStoredOperation(
            foreignRequest.GraphId,
            Assert.IsType<GovernedLoopRevisionOperationEvidence>(foreignCommitted.Evidence));
        foreach (var (generation, expectedStatus) in new[]
        {
            (1L, GovernedLoopRevisionLifecycleMutationStatus.Ambiguous),
            (2L, GovernedLoopRevisionLifecycleMutationStatus.Conflict),
        })
        {
            var foreignStore = new InMemoryStore
            {
                CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                    generation,
                    foreignOperation,
                    missingEvidenceSnapshot),
            };
            var foreignResult = await Service(foreignStore).MutateAsync(changedRequest);
            Assert.Equal(expectedStatus, foreignResult.Status);
            Assert.Equal(1, foreignStore.Commits);
        }
    }

    [Fact]
    public async Task Commit_replay_uses_stored_trusted_evidence_and_rejects_copied_hash_bindings()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "commit-replay-binding",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var storedAtUtc = _now.AddTicks(1);
        var exactStore = new InMemoryStore
        {
            CommitOverride = mutation => CreateDraftReplayResult(
                mutation,
                mutation.Operation.ActorId,
                mutation.Operation.CandidateRevision!,
                storedAtUtc,
                Hash('d')),
        };
        var exactReplay = await Service(exactStore).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, exactReplay.Status);
        Assert.Equal(storedAtUtc, exactReplay.Evidence!.RecordedAtUtc);
        Assert.Equal(Hash('d'), exactReplay.Evidence.AuthorityEvidenceHash);
        Assert.Equal(storedAtUtc, exactReplay.Head!.UpdatedAtUtc);

        var hostileActorStore = new InMemoryStore
        {
            CommitOverride = mutation => CreateDraftReplayResult(
                mutation,
                "actor-foreign",
                mutation.Operation.CandidateRevision!,
                storedAtUtc,
                Hash('d')),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(hostileActorStore).MutateAsync(request)).Status);

        var hostileReferenceStore = new InMemoryStore
        {
            CommitOverride = mutation => CreateDraftReplayResult(
                mutation,
                mutation.Operation.ActorId,
                Revision("graph-a", "revision-foreign", 'f'),
                storedAtUtc,
                Hash('d')),
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(hostileReferenceStore).MutateAsync(request)).Status);

        var omittedReceiptStore = new InMemoryStore
        {
            CommitOverride = mutation => CreateDraftReplayResult(
                mutation,
                mutation.Operation.ActorId,
                mutation.Operation.CandidateRevision!,
                storedAtUtc,
                Hash('d')) with
            {
                Snapshot = CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Replayed, mutation).Snapshot,
            },
        };
        Assert.Equal(
            GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
            (await Service(omittedReceiptStore).MutateAsync(request)).Status);
    }

    [Fact]
    public async Task Three_consecutive_store_conflicts_stop_without_a_fourth_attempt()
    {
        var store = new InMemoryStore { ForcedStoreConflicts = 3 };
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "retry-exhausted",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));

        var result = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, result.Status);
        Assert.Equal(3, store.Commits);
        Assert.Equal(4, store.MutationReads);
    }

    [Fact]
    public async Task Artifact_limit_records_one_durable_bounded_failure_receipt()
    {
        var snapshot = CreateDraftHistory(GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph);
        var store = new InMemoryStore();
        store.Seed("graph-a", snapshot, snapshot.Operations.Count);
        var target = Assert.IsType<GovernedLoopRevisionReference>(snapshot.Head.DraftRevision);
        var request = Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "artifact-limit-receipt",
            snapshot.Head,
            candidate: Revision("graph-a", "revision-over-artifact-limit", 'f'),
            target: target);

        var result = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded, result.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.ArtifactLimitExceeded, result.Evidence!.FailureCode);
        Assert.Equal(snapshot.Head, result.Evidence.PreviousHead);
        Assert.Equal(snapshot.Head, result.Evidence.ResultHead);
        Assert.Equal(1, store.Commits);
        var retained = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(store.Snapshot("graph-a"));
        Assert.Equal(GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph, retained.Artifacts.Count);
        Assert.Equal(snapshot.Operations.Count + 1, retained.Operations.Count);
        Assert.Contains(result.Evidence, retained.Operations);
    }

    [Fact]
    public async Task Last_evidence_slot_is_a_durable_replayable_limit_receipt_then_exhaustion_is_read_only()
    {
        var snapshot = ExtendWithConflictReceipts(
            CreateDraftHistory(1),
            GovernedLoopRevisionContractLimits.MaxOperationsPerGraph - 1);
        var store = new InMemoryStore();
        store.Seed("graph-a", snapshot, snapshot.Operations.Count);
        var target = Assert.IsType<GovernedLoopRevisionReference>(snapshot.Head.DraftRevision);
        var request = Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "last-evidence-limit-receipt",
            snapshot.Head,
            candidate: Revision("graph-a", "revision-last-evidence", 'e'),
            target: target);

        var first = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded, first.Status);
        Assert.Equal(GovernedLoopRevisionOperationFailureCode.EvidenceLimitExceeded, first.Evidence!.FailureCode);
        Assert.Equal(1, store.Commits);
        var fullSnapshot = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(store.Snapshot("graph-a"));
        Assert.Equal(GovernedLoopRevisionContractLimits.MaxOperationsPerGraph, fullSnapshot.Operations.Count);
        Assert.Contains(first.Evidence, fullSnapshot.Operations);

        var replayAuthorizer = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var replay = await Service(store, replayAuthorizer).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(first.Evidence, replay.Evidence);
        Assert.Equal(0, replayAuthorizer.Calls);
        Assert.Equal(1, store.Commits);

        var exhaustedRequest = Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "exhausted-evidence-limit",
            snapshot.Head,
            candidate: Revision("graph-a", "revision-after-exhaustion", 'd'),
            target: target);
        var exhausted = await Service(store).MutateAsync(exhaustedRequest);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded, exhausted.Status);
        Assert.Null(exhausted.Evidence);
        Assert.Equal(snapshot.Head, exhausted.Head);
        Assert.Equal(1, store.Commits);
        var unchanged = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(store.Snapshot("graph-a"));
        Assert.Equal(GovernedLoopRevisionContractLimits.MaxOperationsPerGraph, unchanged.Operations.Count);
        Assert.DoesNotContain(unchanged.Operations, evidence => string.Equals(
            evidence.OperationId,
            exhaustedRequest.OperationId,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_rejects_missing_server_owned_ports()
    {
        var store = new InMemoryStore();
        var authorizer = new StubAuthorizer();
        var validator = new StubPublishValidator();
        var transaction = new StubAuthorityTransaction();

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleService(null!, authorizer, validator, transaction));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleService(store, null!, validator, transaction));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleService(store, authorizer, null!, transaction));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleService(store, authorizer, validator, null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopPublishedRevisionSource(null!, transaction));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopPublishedRevisionSource(store, null!));
    }

    private static GovernedLoopRevisionLifecycleService Service(
        InMemoryStore store,
        StubAuthorizer? authorizer = null,
        StubPublishValidator? validator = null,
        ICapabilityAuthorityTransaction? transaction = null,
        TimeProvider? timeProvider = null)
        => new(
            store,
            authorizer ?? new StubAuthorizer(),
            validator ?? new StubPublishValidator(),
            transaction ?? new StubAuthorityTransaction(),
            timeProvider ?? new TestTimeProvider(_now));

    private static GovernedLoopRevisionLifecycleRequest Request(
        GovernedLoopRevisionOperationKind kind,
        string operationId,
        GovernedLoopRevisionLifecycleHead? expected,
        GovernedLoopRevisionReference? candidate = null,
        GovernedLoopRevisionReference? target = null,
        GovernedLoopRevisionPublicationPin? rollbackSource = null)
    {
        Assert.True(AuthorityActorId.TryParse("actor-owner", out var actor, out _));
        return new GovernedLoopRevisionLifecycleRequest(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            operationId,
            kind,
            candidate?.GraphId ?? target?.GraphId ?? expected?.GraphId ?? "graph-a",
            actor!,
            expected?.Status ?? GovernedLoopRevisionLifecycleStatus.Unknown,
            expected?.LifecycleVersion ?? 0,
            expected?.DraftRevision,
            expected?.PublishedRevision,
            candidate,
            target,
            rollbackSource);
    }

    private static GovernedLoopRevisionLifecycleHead HeadExpectation(
        string graphId,
        long version,
        GovernedLoopRevisionLifecycleStatus status,
        GovernedLoopRevisionReference? draft,
        GovernedLoopRevisionPublicationPin? publication)
        => new(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            graphId,
            version,
            status,
            draft,
            publication,
            "expected-operation",
            _now);

    private static GovernedLoopRevisionReference Revision(string graphId, string revisionId, char hashCharacter)
        => GovernedLoopRevisionReference.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            graphId,
            revisionId,
            Hash(hashCharacter));

    private static string Hash(char character) => new(character, GovernedLoopRevisionContractLimits.Sha256HexCharacters);

    private static GovernedLoopRevisionStoreSnapshot CreateDraftHistory(int artifactCount)
    {
        if (artifactCount is < 1 or > GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactCount));
        }

        var artifacts = new List<GovernedLoopRevisionArtifact>(artifactCount);
        var operations = new List<GovernedLoopRevisionOperationEvidence>(artifactCount);
        GovernedLoopRevisionLifecycleHead? previousHead = null;
        GovernedLoopRevisionReference? previousRevision = null;
        for (var version = 1; version <= artifactCount; version++)
        {
            var operationId = $"seed-operation-{version:D4}";
            var revision = Revision("graph-a", $"seed-revision-{version:D4}", HexCharacter(version));
            var head = new GovernedLoopRevisionLifecycleHead(
                GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
                "graph-a",
                version,
                GovernedLoopRevisionLifecycleStatus.Draft,
                revision,
                null,
                operationId,
                _now);
            var kind = version == 1
                ? GovernedLoopRevisionOperationKind.CreateDraft
                : GovernedLoopRevisionOperationKind.ReplaceDraft;
            var evidence = new GovernedLoopRevisionOperationEvidence(
                GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
                operationId,
                "actor-owner",
                Hash(HexCharacter(version + 1)),
                kind,
                GovernedLoopRevisionOperationOutcome.Committed,
                GovernedLoopRevisionOperationFailureCode.None,
                previousHead,
                head,
                revision,
                previousRevision,
                null,
                _authorityHash,
                null,
                _now);
            var artifact = new GovernedLoopRevisionArtifact(
                GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
                revision,
                previousRevision,
                null,
                operationId,
                "actor-owner",
                _now);
            artifacts.Add(artifact);
            operations.Add(evidence);
            previousHead = head;
            previousRevision = revision;
        }

        return new GovernedLoopRevisionStoreSnapshot(
            previousHead!,
            Array.AsReadOnly(artifacts.ToArray()),
            Array.AsReadOnly(operations.ToArray()));
    }

    private static GovernedLoopRevisionStoreSnapshot ExtendWithConflictReceipts(
        GovernedLoopRevisionStoreSnapshot snapshot,
        int targetOperationCount)
    {
        if (targetOperationCount < snapshot.Operations.Count
            || targetOperationCount > GovernedLoopRevisionContractLimits.MaxOperationsPerGraph)
        {
            throw new ArgumentOutOfRangeException(nameof(targetOperationCount));
        }

        var operations = snapshot.Operations.ToList();
        for (var index = operations.Count + 1; index <= targetOperationCount; index++)
        {
            operations.Add(new GovernedLoopRevisionOperationEvidence(
                GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
                $"retained-conflict-{index:D4}",
                "actor-owner",
                Hash(HexCharacter(index + 2)),
                GovernedLoopRevisionOperationKind.CreateDraft,
                GovernedLoopRevisionOperationOutcome.Conflict,
                GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
                snapshot.Head,
                snapshot.Head,
                Revision("graph-a", $"retained-candidate-{index:D4}", HexCharacter(index + 3)),
                null,
                null,
                _authorityHash,
                null,
                _now));
        }

        return snapshot with { Operations = Array.AsReadOnly(operations.ToArray()) };
    }

    private static char HexCharacter(int value) => "0123456789abcdef"[value & 15];

    private static GovernedLoopRevisionStoreCommitResult CommitResultForMutation(
        GovernedLoopRevisionStoreCommitStatus status,
        GovernedLoopRevisionStoreMutation mutation)
    {
        var snapshot = mutation.HeadToWrite is null
            ? null
            : new GovernedLoopRevisionStoreSnapshot(
                mutation.HeadToWrite,
                Array.AsReadOnly(new[] { mutation.ArtifactToAppend! }),
                Array.AsReadOnly(new[] { mutation.Operation }));
        return new GovernedLoopRevisionStoreCommitResult(
            status,
            mutation.ExpectedStoreGeneration + 1,
            new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation),
            snapshot);
    }

    private static GovernedLoopRevisionStoreCommitResult CreateDraftReplayResult(
        GovernedLoopRevisionStoreMutation mutation,
        string actorId,
        GovernedLoopRevisionReference candidateRevision,
        DateTimeOffset recordedAtUtc,
        string authorityEvidenceHash)
    {
        var head = mutation.Operation.ResultHead! with
        {
            DraftRevision = candidateRevision,
            UpdatedAtUtc = recordedAtUtc,
        };
        var evidence = mutation.Operation with
        {
            ActorId = actorId,
            CandidateRevision = candidateRevision,
            ResultHead = head,
            AuthorityEvidenceHash = authorityEvidenceHash,
            RecordedAtUtc = recordedAtUtc,
        };
        var artifact = mutation.ArtifactToAppend! with
        {
            Revision = candidateRevision,
            CreatedByActorId = actorId,
            CreatedAtUtc = recordedAtUtc,
        };
        return new GovernedLoopRevisionStoreCommitResult(
            GovernedLoopRevisionStoreCommitStatus.Replayed,
            mutation.ExpectedStoreGeneration + 1,
            new GovernedLoopRevisionStoredOperation(mutation.GraphId, evidence),
            new GovernedLoopRevisionStoreSnapshot(
                head,
                Array.AsReadOnly(new[] { artifact }),
                Array.AsReadOnly(new[] { evidence })));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _utcNow = now;

        internal void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new IOException("clock unavailable");
    }

    private sealed class StubAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        internal int Executions { get; private set; }
        internal bool Throw { get; set; }
        internal bool ThrowAfterCallback { get; set; }

        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            Executions++;
            if (Throw)
            {
                throw new IOException("fence unavailable");
            }

            var result = await operation(cancellationToken);
            if (ThrowAfterCallback)
            {
                throw new IOException("fence disposal failed");
            }

            return result;
        }

        public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
            Func<CancellationToken, Task<bool>> validator,
            CancellationToken cancellationToken = default)
            => await validator(cancellationToken) ? new StubAuthorityLease() : null;
    }

    private sealed class StubAuthorityLease : ICapabilityAuthorityLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        internal int Calls { get; private set; }
        internal GovernedLoopRevisionActorAuthorizationStatus Status { get; set; } = GovernedLoopRevisionActorAuthorizationStatus.Authorized;
        internal Queue<GovernedLoopRevisionActorAuthorizationStatus> Statuses { get; } = new();
        internal bool CorruptRequestHash { get; set; }
        internal bool CorruptOperationId { get; set; }
        internal bool CorruptActor { get; set; }
        internal bool InvalidEvidenceHash { get; set; }
        internal bool ReturnNull { get; set; }
        internal bool Throw { get; set; }
        internal Action<int>? OnCall { get; set; }
        internal List<GovernedLoopRevisionActorAuthorizationRequest> Requests { get; } = new();

        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            OnCall?.Invoke(Calls);
            if (Throw)
            {
                throw new IOException("authority unavailable");
            }

            if (ReturnNull)
            {
                return Task.FromResult<GovernedLoopRevisionActorAuthorization>(null!);
            }

            var actor = request.Request.ActorId;
            if (CorruptActor)
            {
                Assert.True(AuthorityActorId.TryParse("actor-foreign", out var foreignActor, out _));
                actor = foreignActor!;
            }

            var status = Statuses.TryDequeue(out var queued) ? queued : Status;
            return Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                status,
                CorruptOperationId ? "operation-foreign" : request.Request.OperationId,
                CorruptRequestHash ? Hash('f') : request.RequestHash,
                actor,
                InvalidEvidenceHash ? "bad-hash" : _authorityHash));
        }
    }

    private sealed class StubPublishValidator : IGovernedLoopRevisionPublishValidator
    {
        internal GovernedLoopRevisionPublishValidationRequest? LastRequest { get; private set; }
        internal GovernedLoopRevisionPublishValidationStatus Status { get; set; } = GovernedLoopRevisionPublishValidationStatus.Valid;
        internal bool CorruptRevision { get; set; }
        internal bool CorruptOperationId { get; set; }
        internal bool CorruptRequestHash { get; set; }
        internal bool InvalidEvidenceHash { get; set; }
        internal bool ReturnNull { get; set; }
        internal bool Throw { get; set; }

        public Task<GovernedLoopRevisionPublishValidation> ValidateAsync(
            GovernedLoopRevisionPublishValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Throw)
            {
                throw new IOException("validation unavailable");
            }

            if (ReturnNull)
            {
                return Task.FromResult<GovernedLoopRevisionPublishValidation>(null!);
            }

            var revision = CorruptRevision
                ? Revision(request.Artifact.Revision.GraphId, "hostile-revision", 'f')
                : request.Artifact.Revision;
            return Task.FromResult(new GovernedLoopRevisionPublishValidation(
                Status,
                CorruptOperationId ? "operation-foreign" : request.OperationId,
                CorruptRequestHash ? Hash('f') : request.RequestHash,
                revision,
                InvalidEvidenceHash ? "bad-hash" : _validationHash));
        }
    }

    [Fact]
    public async Task Commit_io_uncertainty_is_ambiguous_but_pre_intent_caller_cancellation_propagates()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "post-intent-failure",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        var store = new InMemoryStore { CommitException = new IOException("crashed after intent") };

        var ioResult = await Service(store).MutateAsync(request);

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, ioResult.Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store = new InMemoryStore { CommitException = new OperationCanceledException(cancellation.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(store).MutateAsync(request, cancellation.Token));
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task Cancellation_before_commit_and_authority_fence_failures_preserve_safe_typed_outcomes()
    {
        var request = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "cancel-before-intent",
            null,
            candidate: Revision("graph-a", "revision-1", '1'));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transaction = new CancellingAuthorityTransaction();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new InMemoryStore(), transaction: transaction).MutateAsync(request, cancellation.Token));

        var unavailableTransaction = new StubAuthorityTransaction { Throw = true };
        var unavailable = await Service(new InMemoryStore(), transaction: unavailableTransaction).MutateAsync(request);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Post_callback_fence_failure_preserves_exact_durable_proof_and_ambiguates_unproved_reads()
    {
        var store = new InMemoryStore();
        var revision = Revision("graph-a", "revision-1", '1');
        var committed = await Service(
            store,
            transaction: new StubAuthorityTransaction { ThrowAfterCallback = true }).MutateAsync(Request(
                GovernedLoopRevisionOperationKind.CreateDraft,
                "post-callback-create",
                null,
                candidate: revision));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, committed.Status);
        Assert.NotNull(committed.Evidence);
        Assert.Equal(1, store.Commits);

        var contradictoryStore = new InMemoryStore
        {
            CommitOverride = mutation => CommitResultForMutation(GovernedLoopRevisionStoreCommitStatus.Ambiguous, mutation),
        };
        var contradictory = await Service(
            contradictoryStore,
            transaction: new StubAuthorityTransaction { ThrowAfterCallback = true }).MutateAsync(Request(
                GovernedLoopRevisionOperationKind.CreateDraft,
                "post-callback-contradiction",
                null,
                candidate: revision));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, contradictory.Status);
        Assert.Null(contradictory.Evidence);

        var conflict = await Service(
            store,
            transaction: new StubAuthorityTransaction { ThrowAfterCallback = true }).MutateAsync(Request(
                GovernedLoopRevisionOperationKind.CreateDraft,
                "post-callback-conflict",
                null,
                candidate: Revision("graph-a", "revision-2", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, conflict.Status);
        Assert.Equal(GovernedLoopRevisionOperationOutcome.Conflict, conflict.Evidence!.Outcome);

        var published = await Service(store).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Publish,
            "post-callback-publish",
            committed.Head,
            target: revision));
        var exactResolution = await new GovernedLoopPublishedRevisionSource(
            store,
            new StubAuthorityTransaction { ThrowAfterCallback = true }).ResolveAsync(published.Head!.PublishedRevision);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Active, exactResolution.Status);

        var missingPin = new GovernedLoopRevisionPublicationPin(
            1,
            Revision("graph-missing", "revision-1", '1'),
            "publish-missing",
            Hash('a'));
        var ambiguousResolution = await new GovernedLoopPublishedRevisionSource(
            store,
            new StubAuthorityTransaction { ThrowAfterCallback = true }).ResolveAsync(missingPin);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, ambiguousResolution.Status);
    }

    [Fact]
    public async Task Resolver_rejects_null_overbound_and_ready_generation_zero_without_echoing_hostile_input()
    {
        var store = new InMemoryStore();
        var resolver = new GovernedLoopPublishedRevisionSource(store, new StubAuthorityTransaction());
        var nullResult = await resolver.ResolveAsync(null);
        var validRevision = Revision("graph-a", "revision-1", '1');
        var hostile = new GovernedLoopRevisionPublicationPin(
            1,
            validRevision,
            new string('x', GovernedLoopRevisionContractLimits.MaxIdentifierCharacters + 1),
            Hash('c'));
        var hostileResult = await resolver.ResolveAsync(hostile);

        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Invalid, nullResult.Status);
        Assert.Null(nullResult.RequestedPin);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Invalid, hostileResult.Status);
        Assert.Null(hostileResult.RequestedPin);
        Assert.DoesNotContain("xxxxxxxx", hostileResult.ToString(), StringComparison.Ordinal);

        var pin = new GovernedLoopRevisionPublicationPin(1, validRevision, "publish-1", Hash('d'));
        store.GraphReadOverride = _ => new GovernedLoopRevisionGraphReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            0,
            null);
        var zeroGeneration = await resolver.ResolveAsync(pin);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, zeroGeneration.Status);

        var fenceFailure = await new GovernedLoopPublishedRevisionSource(store, new StubAuthorityTransaction { Throw = true }).ResolveAsync(hostile);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, fenceFailure.Status);
        Assert.Null(fenceFailure.RequestedPin);
    }

    [Fact]
    public async Task Resolver_requires_committed_publication_evidence_not_artifact_existence_alone()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: revision));
        var fabricatedPin = new GovernedLoopRevisionPublicationPin(1, revision, "fabricated-publish", Hash('c'));

        var result = await new GovernedLoopPublishedRevisionSource(store, new StubAuthorityTransaction()).ResolveAsync(fabricatedPin);

        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Artifact);
        Assert.Equal(created.Head!.LifecycleVersion, result.ObservedLifecycleVersion);
    }

    [Fact]
    public async Task Every_snapshot_observation_requires_operation_count_within_global_generation()
    {
        var historyStore = new InMemoryStore();
        var revision = Revision("graph-a", "revision-1", '1');
        var created = await Service(historyStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "causal-create",
            null,
            candidate: revision));
        var published = await Service(historyStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Publish,
            "causal-publish",
            created.Head,
            target: revision));
        var snapshot = Assert.IsType<GovernedLoopRevisionStoreSnapshot>(historyStore.Snapshot("graph-a"));
        Assert.Equal(2, snapshot.Operations.Count);

        var lifecycleStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                1,
                snapshot,
                null),
        };
        var authorizer = new StubAuthorizer();
        var lifecycleResult = await Service(lifecycleStore, authorizer).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.Archive,
            "causal-lifecycle-read",
            published.Head,
            target: revision));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, lifecycleResult.Status);
        Assert.Equal(0, authorizer.Calls);

        var foreignStore = new InMemoryStore();
        var foreignRequest = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "causal-foreign-collision",
            null,
            candidate: Revision("graph-foreign", "revision-foreign", 'f'));
        var foreignCommitted = await Service(foreignStore).MutateAsync(foreignRequest);
        var foreignOperation = new GovernedLoopRevisionStoredOperation(
            foreignRequest.GraphId,
            Assert.IsType<GovernedLoopRevisionOperationEvidence>(foreignCommitted.Evidence));
        var collisionStore = new InMemoryStore
        {
            MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                snapshot.Operations.Count,
                snapshot,
                foreignOperation),
        };
        var collisionAuthorizer = new StubAuthorizer();
        var collisionResult = await Service(collisionStore, collisionAuthorizer).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            foreignRequest.OperationId,
            null,
            candidate: Revision("graph-a", "revision-new", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, collisionResult.Status);
        Assert.Equal(0, collisionAuthorizer.Calls);

        var pinStore = new InMemoryStore
        {
            GraphReadOverride = _ => new GovernedLoopRevisionGraphReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                1,
                snapshot),
        };
        var pinResult = await new GovernedLoopPublishedRevisionSource(
            pinStore,
            new StubAuthorityTransaction()).ResolveAsync(published.Head!.PublishedRevision);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, pinResult.Status);

        var commitStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                1,
                null,
                snapshot),
        };
        var commitResult = await Service(commitStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "causal-commit-result",
            null,
            candidate: Revision("graph-a", "revision-new", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, commitResult.Status);
        Assert.Equal(1, commitStore.Commits);

        var operationConflictStore = new InMemoryStore
        {
            CommitOverride = mutation =>
            {
                var foreignCandidate = Revision("graph-foreign", "revision-foreign", 'f');
                var foreignHead = mutation.Operation.ResultHead! with
                {
                    GraphId = foreignCandidate.GraphId,
                    DraftRevision = foreignCandidate,
                };
                var foreignEvidence = mutation.Operation with
                {
                    CandidateRevision = foreignCandidate,
                    ResultHead = foreignHead,
                };
                Assert.True(GovernedLoopRevisionContractValidator.Validate(foreignEvidence).IsValid);
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                    1,
                    new GovernedLoopRevisionStoredOperation(foreignCandidate.GraphId, foreignEvidence),
                    snapshot);
            },
        };
        var operationConflictResult = await Service(operationConflictStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "causal-operation-conflict",
            null,
            candidate: Revision("graph-a", "revision-new", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, operationConflictResult.Status);
        Assert.Equal(1, operationConflictStore.Commits);
    }

    [Fact]
    public async Task Resolver_maps_absent_unavailable_ambiguous_null_and_throwing_graph_reads()
    {
        var pin = new GovernedLoopRevisionPublicationPin(
            1,
            Revision("graph-a", "revision-1", '1'),
            "publish-1",
            Hash('a'));
        var store = new InMemoryStore();
        var resolver = new GovernedLoopPublishedRevisionSource(store, new StubAuthorityTransaction());

        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.NotFound, (await resolver.ResolveAsync(pin)).Status);

        store.GraphReadOverride = _ => new GovernedLoopRevisionGraphReadResult(
            GovernedLoopRevisionStoreReadStatus.Unavailable,
            0,
            null);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, (await resolver.ResolveAsync(pin)).Status);

        store.GraphReadOverride = _ => new GovernedLoopRevisionGraphReadResult(
            GovernedLoopRevisionStoreReadStatus.Ambiguous,
            0,
            null);
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, (await resolver.ResolveAsync(pin)).Status);

        store.GraphReadOverride = _ => null!;
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, (await resolver.ResolveAsync(pin)).Status);

        store.GraphReadOverride = null;
        store.GraphReadException = new IOException("graph read unavailable");
        Assert.Equal(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, (await resolver.ResolveAsync(pin)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store.GraphReadException = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(pin, cancellation.Token));
    }

    [Fact]
    public async Task Hostile_lazy_and_overbound_snapshots_fail_closed_without_trusting_count()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var first = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first));
        var valid = store.Snapshot("graph-a")!;
        store.MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            store.StoreGeneration,
            new GovernedLoopRevisionStoreSnapshot(
                valid.Head,
                new CountThrowingReadOnlyList<GovernedLoopRevisionArtifact>(valid.Artifacts),
                new CountThrowingReadOnlyList<GovernedLoopRevisionOperationEvidence>(valid.Operations)),
            null);

        var second = Revision("graph-a", "revision-2", '2');
        var countIndependent = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "replace-count-safe",
            created.Head,
            candidate: second,
            target: first));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Committed, countIndependent.Status);

        store.MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            store.StoreGeneration,
            new GovernedLoopRevisionStoreSnapshot(
                valid.Head,
                new ThrowingReadOnlyList<GovernedLoopRevisionArtifact>(),
                valid.Operations),
            null);
        var throwing = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "replace-throwing",
            countIndependent.Head,
            candidate: Revision("graph-a", "revision-3", '3'),
            target: second));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, throwing.Status);

        store.MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            store.StoreGeneration,
            new GovernedLoopRevisionStoreSnapshot(
                valid.Head,
                new RepeatedReadOnlyList<GovernedLoopRevisionArtifact>(valid.Artifacts[0], GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph + 1),
                valid.Operations),
            null);
        var overbound = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "replace-overbound",
            countIndependent.Head,
            candidate: Revision("graph-a", "revision-4", '4'),
            target: second));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, overbound.Status);
    }

    [Fact]
    public async Task Forked_or_reordered_append_history_is_rejected_even_when_each_record_is_valid()
    {
        var store = new InMemoryStore();
        var service = Service(store);
        var first = Revision("graph-a", "revision-1", '1');
        var created = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "create-1", null, candidate: first));
        var second = Revision("graph-a", "revision-2", '2');
        var replaced = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "replace-2", created.Head, candidate: second, target: first));
        var third = Revision("graph-a", "revision-3", '3');
        var replacedAgain = await service.MutateAsync(Request(GovernedLoopRevisionOperationKind.ReplaceDraft, "replace-3", replaced.Head, candidate: third, target: second));
        var snapshot = store.Snapshot("graph-a")!;
        Assert.Equal(3, snapshot.Operations.Count);
        var reorderedOperations = new[] { snapshot.Operations[0], snapshot.Operations[2], snapshot.Operations[1] };
        store.MutationReadOverride = (_, _, _) => new GovernedLoopRevisionStoreReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            store.StoreGeneration,
            new GovernedLoopRevisionStoreSnapshot(snapshot.Head, snapshot.Artifacts, reorderedOperations),
            null);

        var result = await service.MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "replace-forked",
            replacedAgain.Head,
            candidate: Revision("graph-a", "revision-4", '4'),
            target: third));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Equal(3, store.Commits);
    }

    [Fact]
    public async Task Same_revision_id_with_changed_hash_is_invalid_before_authority()
    {
        var existing = Revision("graph-a", "revision-1", '1');
        var changedHash = Revision("graph-a", "revision-1", '2');
        var expected = HeadExpectation("graph-a", 1, GovernedLoopRevisionLifecycleStatus.Draft, existing, null);
        var authorizer = new StubAuthorizer();

        var result = await Service(new InMemoryStore(), authorizer).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            "same-id-new-hash",
            expected,
            candidate: changedHash,
            target: existing));

        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == GovernedLoopRevisionLifecycleValidationErrorCode.CandidateNotDistinct);
        Assert.Equal(0, authorizer.Calls);
    }

    [Fact]
    public async Task Foreign_operation_conflict_and_ambiguous_results_never_disclose_foreign_evidence()
    {
        var store = new InMemoryStore();
        var first = Revision("graph-a", "revision-1", '1');
        var committed = await Service(store).MutateAsync(Request(GovernedLoopRevisionOperationKind.CreateDraft, "shared-operation", null, candidate: first));
        var foreignRequest = Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "shared-operation",
            null,
            candidate: Revision("graph-b", "revision-1", '2'));

        var readConflict = await Service(store).MutateAsync(foreignRequest);
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, readConflict.Status);
        Assert.Null(readConflict.Evidence);

        var foreignHead = committed.Evidence!.ResultHead! with { LastOperationId = "different-operation" };
        var foreignEvidence = committed.Evidence with
        {
            OperationId = "different-operation",
            ResultHead = foreignHead,
        };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(foreignEvidence).IsValid);
        var foreignOperation = new GovernedLoopRevisionStoredOperation("graph-a", foreignEvidence);
        var isolatedStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Ambiguous,
                1,
                foreignOperation,
                null),
        };
        var ambiguous = await Service(isolatedStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "different-operation",
            null,
            candidate: Revision("graph-b", "revision-1", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.Evidence);

        isolatedStore = new InMemoryStore
        {
            CommitOverride = _ => new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                1,
                foreignOperation,
                null),
        };
        var operationConflict = await Service(isolatedStore).MutateAsync(Request(
            GovernedLoopRevisionOperationKind.CreateDraft,
            "different-operation",
            null,
            candidate: Revision("graph-b", "revision-1", '2')));
        Assert.Equal(GovernedLoopRevisionLifecycleMutationStatus.Conflict, operationConflict.Status);
        Assert.Null(operationConflict.Evidence);
    }

    private sealed class CancellingAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => Task.FromCanceled<TResult>(cancellationToken);

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
            Func<CancellationToken, Task<bool>> validator,
            CancellationToken cancellationToken = default)
            => Task.FromCanceled<ICapabilityAuthorityLease?>(cancellationToken);
    }

    private sealed class InMemoryStore : IGovernedLoopRevisionLifecycleStore
    {
        private readonly Dictionary<string, GovernedLoopRevisionStoreSnapshot> _graphs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GovernedLoopRevisionStoredOperation> _operations = new(StringComparer.Ordinal);

        internal int MutationReads { get; private set; }
        internal int Commits { get; private set; }
        internal long StoreGeneration { get; private set; }
        internal int ForcedStoreConflicts { get; set; }
        internal Exception? CommitException { get; set; }
        internal Exception? MutationReadException { get; set; }
        internal Exception? GraphReadException { get; set; }
        internal GovernedLoopRevisionStoreMutation? LastMutation { get; private set; }
        internal List<string> ReadRequestHashes { get; } = new();
        internal Func<string, GovernedLoopRevisionGraphReadResult>? GraphReadOverride { get; set; }
        internal Func<string, string, string, GovernedLoopRevisionStoreReadResult>? MutationReadOverride { get; set; }
        internal Func<GovernedLoopRevisionStoreMutation, GovernedLoopRevisionStoreCommitResult>? CommitOverride { get; set; }

        internal GovernedLoopRevisionStoreSnapshot? Snapshot(string graphId)
            => _graphs.TryGetValue(graphId, out var snapshot) ? snapshot : null;

        internal void Seed(
            string graphId,
            GovernedLoopRevisionStoreSnapshot snapshot,
            long storeGeneration)
        {
            _graphs.Add(graphId, snapshot);
            foreach (var evidence in snapshot.Operations)
            {
                _operations.Add(evidence.OperationId, new GovernedLoopRevisionStoredOperation(graphId, evidence));
            }

            StoreGeneration = storeGeneration;
        }

        public Task<GovernedLoopRevisionGraphReadResult> ReadGraphAsync(
            string graphId,
            CancellationToken cancellationToken = default)
        {
            if (GraphReadException is not null)
            {
                throw GraphReadException;
            }

            if (GraphReadOverride is not null)
            {
                return Task.FromResult(GraphReadOverride(graphId));
            }

            return Task.FromResult(_graphs.TryGetValue(graphId, out var snapshot)
                ? new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Ready, StoreGeneration, snapshot)
                : new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, StoreGeneration, null));
        }

        public Task<GovernedLoopRevisionStoreReadResult> ReadForMutationAsync(
            string graphId,
            string operationId,
            string requestHash,
            CancellationToken cancellationToken = default)
        {
            MutationReads++;
            ReadRequestHashes.Add(requestHash);
            if (MutationReadException is not null)
            {
                throw MutationReadException;
            }

            if (MutationReadOverride is not null)
            {
                return Task.FromResult(MutationReadOverride(graphId, operationId, requestHash));
            }

            _operations.TryGetValue(operationId, out var existing);
            return Task.FromResult(_graphs.TryGetValue(graphId, out var snapshot)
                ? new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Ready, StoreGeneration, snapshot, existing)
                : new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, StoreGeneration, null, existing));
        }

        public Task<GovernedLoopRevisionStoreCommitResult> CommitAsync(
            GovernedLoopRevisionStoreMutation mutation,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            LastMutation = mutation;
            if (CommitException is not null)
            {
                throw CommitException;
            }

            if (CommitOverride is not null)
            {
                return Task.FromResult(CommitOverride(mutation));
            }

            if (ForcedStoreConflicts > 0)
            {
                ForcedStoreConflicts--;
                StoreGeneration++;
                return Task.FromResult(new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                    StoreGeneration,
                    null,
                    null));
            }

            if (_operations.TryGetValue(mutation.Operation.OperationId, out var existing))
            {
                var status = string.Equals(existing.GraphId, mutation.GraphId, StringComparison.Ordinal)
                    && string.Equals(existing.Evidence.RequestHash, mutation.Operation.RequestHash, StringComparison.Ordinal)
                    ? GovernedLoopRevisionStoreCommitStatus.Replayed
                    : GovernedLoopRevisionStoreCommitStatus.OperationConflict;
                _graphs.TryGetValue(mutation.GraphId, out var replaySnapshot);
                return Task.FromResult(new GovernedLoopRevisionStoreCommitResult(status, StoreGeneration, existing, replaySnapshot));
            }

            if (mutation.ExpectedStoreGeneration != StoreGeneration)
            {
                return Task.FromResult(new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                    StoreGeneration,
                    null,
                    null));
            }

            var storedOperation = new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation);
            _operations.Add(mutation.Operation.OperationId, storedOperation);
            _graphs.TryGetValue(mutation.GraphId, out var current);
            GovernedLoopRevisionStoreSnapshot? next = null;
            if (mutation.HeadToWrite is not null)
            {
                var artifacts = current?.Artifacts.ToList() ?? new List<GovernedLoopRevisionArtifact>();
                if (mutation.ArtifactToAppend is not null)
                {
                    artifacts.Add(mutation.ArtifactToAppend);
                }

                var operations = current?.Operations.ToList() ?? new List<GovernedLoopRevisionOperationEvidence>();
                operations.Add(mutation.Operation);
                next = new GovernedLoopRevisionStoreSnapshot(
                    mutation.HeadToWrite,
                    Array.AsReadOnly(artifacts.ToArray()),
                    Array.AsReadOnly(operations.ToArray()));
                _graphs[mutation.GraphId] = next;
            }
            else if (current is not null)
            {
                var operations = current.Operations.Append(mutation.Operation).ToArray();
                next = current with { Operations = Array.AsReadOnly(operations) };
                _graphs[mutation.GraphId] = next;
            }

            StoreGeneration++;
            return Task.FromResult(new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Committed,
                StoreGeneration,
                storedOperation,
                next));
        }
    }

    private sealed class CountThrowingReadOnlyList<T>(IEnumerable<T> values) : IReadOnlyList<T>
    {
        private readonly T[] _values = values.ToArray();
        public int Count => throw new InvalidOperationException("Count must not be trusted.");
        public T this[int index] => _values[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Count unavailable.");
        public T this[int index] => throw new InvalidOperationException("Indexer unavailable.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration failed.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RepeatedReadOnlyList<T>(T value, int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => index >= 0 && index < count ? value : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                yield return value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
