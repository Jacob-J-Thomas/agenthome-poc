using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopDefinitionReceiptRetentionTests
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _observedAtUtc = _createdAtUtc.AddDays(40);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Expired_update_receipt_compacts_to_proof_without_unbounding_routine_definition_reads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new RecordingAuditLog();
        var store = new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc));
        var original = CreateDefinition("loop-retained");
        await CreateCommittedAsync(store, original);
        var updated = Advance(original, "update-retained", _createdAtUtc.AddDays(1));
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, updated, original, 1, updated.UpdatedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, (await store.UpdateAsync(updated, 1, mutation)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));

        var result = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-update"));
        var lookup = await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, mutation.OperationId);
        var replay = await store.UpdateAsync(updated, 1, mutation);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, result.Status);
        Assert.Equal(1, result.CompactedArtifactCount);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, lookup.Status);
        Assert.Equal(mutation.RequestHash, lookup.ExpiredProof!.RequestHash);
        Assert.Equal(CustomLoopDefinitionStoreStatus.OperationConflict, replay.Status);
        Assert.Equal(updated.ContentHash, (await store.GetAsync(updated.Id))!.ContentHash);
        Assert.Single(await store.ListAsync());
        Assert.Equal(2, audit.Events.Count);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, mutation.OperationId + ".json")));
    }

    [Fact]
    public async Task Definition_reads_accept_shared_lifecycle_retention_lock_and_cleanup_journal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var definitionStore = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-shared-lifecycle-retention");
        await CreateCommittedAsync(definitionStore, definition);
        var lifecycleStore = new CustomLoopControlOperationStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var cleanup = await lifecycleStore.CleanupAsync(new CustomLoopReceiptCleanupCommand(
            CustomLoopReceiptCleanupCommand.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            "cleanup-shared-lifecycle-retention",
            AuditSchema.Actors.Web,
            "web",
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount,
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes));

        var loaded = await definitionStore.GetAsync(definition.Id);

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, cleanup.Status);
        Assert.True(Directory.Exists(paths.CustomLoopControlReceiptCleanupPath));
        Assert.NotNull(loaded);
        Assert.Equal(definition.ContentHash, loaded.ContentHash);
    }

    [Fact]
    public async Task Expired_create_receipt_for_a_live_definition_is_retained_as_live_lineage_not_reported_compactable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-live-create-lineage");
        await CreateCommittedAsync(store, definition);

        var posture = await store.InspectReceiptRetentionAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-live-create-lineage"));

        Assert.Equal(1, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.RetainedLiveLineage).ArtifactCount);
        Assert.Equal(0, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Compactable).ArtifactCount);
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, cleanup.Status);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, definition.LastMutationOperationId + ".json")));
    }

    [Fact]
    public async Task Tombstone_lookup_never_accepts_an_update_receipt_or_its_compact_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);

        var rawLookup = await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, operationId);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Unknown, rawLookup.Status);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, (await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-update-for-tombstone-lookup"))).Status);
        var compactLookup = await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, operationId);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Unknown, compactLookup.Status);
    }

    [Fact]
    public async Task Failed_delete_receipt_compacts_without_fabricating_deleted_lineage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var mutation = new CustomLoopDefinitionMutationRequest(
            CustomLoopDefinitionMutationKind.Delete,
            "delete-missing",
            new string('a', CustomLoopLimits.Sha256HexCharacters),
            "loop-missing",
            "default-assistant",
            1,
            null,
            null,
            _createdAtUtc);

        Assert.Equal(CustomLoopDefinitionStoreStatus.NotFound, (await store.DeleteAsync(mutation.LoopId, 1, mutation.OperationId, mutation.RequestedAtUtc, mutation)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));
        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, (await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-failed-delete"))).Status);

        var ledger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        var proof = Assert.Single(ledger.ExpiredOperations);
        Assert.Equal(CustomLoopDefinitionStoreStatus.NotFound, proof.DefinitionMutationOutcome);
        Assert.Null(proof.DeleteLineageBindingHash);
        Assert.Empty(ledger.DefinitionLineage);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, mutation.OperationId)).Status);
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Unknown, (await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, mutation.OperationId)).Status);
    }

    [Fact]
    public void Admission_accounts_for_retained_and_outstanding_raw_proof_obligations_and_preserves_the_exact_reason()
    {
        var countBudget = new CustomLoopReceiptRetentionBudget(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, 10, 1_000, 1, 100, 2, 100);
        var countReason = countBudget.GetProofAdmissionExhaustionReason(1, 10, 1, 10, 1, 10);
        var byteBudget = countBudget with { MaximumProofCount = 10, MaximumProofUtf8Bytes = 31 };
        var byteReason = byteBudget.GetProofAdmissionExhaustionReason(1, 10, 1, 10, 1, 10);

        Assert.Equal(CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit, countReason);
        Assert.Equal(CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit, byteReason);
        Assert.Equal(byteReason, CustomLoopDefinitionStoreResult.LimitExceeded(byteReason).RetentionExhaustionReason);
    }

    [Fact]
    public async Task Tombstone_compaction_preserves_deleted_lineage_and_prevents_loop_identity_reuse()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-deleted");
        await CreateCommittedAsync(store, definition);
        var deletedAtUtc = _createdAtUtc.AddDays(1);
        var deletion = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-retained", null, definition, 1, deletedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, (await store.DeleteAsync(definition.Id, 1, deletion.OperationId, deletedAtUtc, deletion)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(deletion.OperationId));

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionTombstone, "cleanup-tombstone"));
        var replacement = CustomLoopDefinitionContentHash.Apply(CreateDefinition(definition.Id) with { LastMutationOperationId = "create-reused" });
        var reuse = await store.CreateAsync(replacement);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, cleanup.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json")));
        Assert.Equal(CustomLoopDefinitionStoreStatus.Conflict, reuse.Status);
        Assert.NotNull(reuse.Tombstone);
        Assert.Equal(definition.Id, reuse.Tombstone!.LoopId);
        Assert.Null(await store.GetAsync(definition.Id));
    }

    [Fact]
    public async Task Compatibility_delete_persists_a_delete_receipt_that_allows_tombstone_compaction()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-compat-delete");
        await CreateCommittedAsync(store, definition);

        var deleted = await store.DeleteAsync(definition.Id, definition.DefinitionVersion, "delete-compat", _createdAtUtc.AddDays(1));
        var operation = await store.GetMutationOperationAsync("delete-compat");
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, deleted.Status);
        Assert.NotNull(operation.Operation);
        Assert.Equal(CustomLoopDefinitionMutationKind.Delete, operation.Operation!.Kind);
        var priorDefinition = Assert.IsType<CustomLoopDefinition>(operation.Operation.PriorDefinition);
        Assert.Equal(definition.Id, priorDefinition.Id);
        Assert.Equal(definition.DefinitionVersion, priorDefinition.DefinitionVersion);
        Assert.Equal(definition.ContentHash, priorDefinition.ContentHash);
        Assert.True(CustomLoopDefinitionContentHash.Matches(priorDefinition));
        Assert.Equal(CustomLoopDefinitionStoreStatus.AlreadyDeleted, (await store.DeleteAsync(definition.Id, definition.DefinitionVersion, "delete-compat", _createdAtUtc.AddDays(2))).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync("delete-compat"));

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionTombstone, "cleanup-compat-delete"));
        var receiptCleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-compat-delete-receipt"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, receiptCleanup.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json")));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, "delete-compat")).Status);
    }

    [Fact]
    public async Task Intent_audit_failure_is_distinct_and_preserves_the_exact_receipt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new RecordingAuditLog { FailOnAppend = 1 };
        var store = new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-audit-failure"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.AuditUnavailable, cleanup.BlockReason);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Candidate_change_after_durable_intent_reports_cleanup_conflict_without_deleting_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new MutatingIntentAuditLog(paths), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-conflict"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.CleanupConflict, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CleanupConflict, cleanup.BlockReason);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
        Assert.NotNull(cleanup.Journal!.ProofLedgerHash);
        var posture = await store.InspectReceiptRetentionAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        Assert.Equal(1, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Ambiguous).ArtifactCount);
        await Assert.ThrowsAsync<FormatException>(() => store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, operationId));
    }

    [Fact]
    public async Task Outcome_audit_failure_retains_compact_proof_and_does_not_repeat_the_uncertain_append()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new RecordingAuditLog { FailOnAppend = 2 };
        var store = new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var request = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-outcome-warning");

        var cleanup = await store.CleanupReceiptRetentionAsync(request);
        var replay = await new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc.AddMinutes(1))).CleanupReceiptRetentionAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, replay.Status);
        Assert.False(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, operationId)).Status);
        Assert.Equal(2, audit.Attempts);
    }

    [Fact]
    public async Task Timestamp_free_cleanup_replays_after_time_advances_and_completed_identity_survives_journal_rotation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableTimeProvider(_createdAtUtc);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), clock);
        var definition = CreateDefinition("loop-cleanup-history");
        await CreateCommittedAsync(store, definition);
        var port = store.CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var firstCommand = CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-a");

        var first = await port.CleanupAsync(firstCommand);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-b"));

        var updatedAtUtc = _createdAtUtc.AddMinutes(2);
        var updated = Advance(definition, "update-after-cleanup-a", updatedAtUtc);
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, updated, definition, definition.DefinitionVersion, updatedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, (await store.UpdateAsync(updated, definition.DefinitionVersion, mutation)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));
        clock.Advance(TimeSpan.FromDays(40));

        var delayedReplay = await port.CleanupAsync(firstCommand);
        var changedReuse = await port.CleanupAsync(firstCommand with { Surface = "cli" });
        var posture = await port.InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, first.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, second.Status);
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, delayedReplay.Status);
        Assert.Equal(first.Journal, delayedReplay.Journal);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Invalid, changedReuse.Status);
        Assert.Equal(1, posture.CompletedCleanupOperationCount);
        Assert.True(posture.CompletedCleanupHistoryUtf8Bytes > 0);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, mutation.OperationId + ".json")));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionMutationReceiptCleanupHistoryPath, firstCommand.OperationId + ".json")));
    }

    [Fact]
    public async Task Cleanup_history_inventory_fails_closed_on_noncanonical_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableTimeProvider(_createdAtUtc);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), clock);
        var port = store.CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, (await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-valid-a"))).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, (await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-valid-b"))).Status);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopDefinitionMutationReceiptCleanupHistoryPath, "unexpected.JSON"), "{}");

        var cleanup = await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-blocked"));
        var posture = await port.InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.Contains(posture.Categories, item => item.Category == CustomLoopReceiptArtifactCategory.Corrupt && item.ArtifactCount > 0);
    }

    [Fact]
    public async Task Cleanup_history_semantic_contract_corruption_fails_closed_in_cleanup_and_posture()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new MutableTimeProvider(_createdAtUtc);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), clock);
        var port = store.CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var firstCommand = CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-semantic-a");

        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, (await port.CleanupAsync(firstCommand)).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CustomLoopReceiptCleanupStatus.NothingEligible, (await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-semantic-b"))).Status);

        var historyPath = Path.Combine(paths.CustomLoopDefinitionMutationReceiptCleanupHistoryPath, firstCommand.OperationId + ".json");
        var persistedJournal = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(await File.ReadAllBytesAsync(historyPath));
        var mismatchedRequestHash = new string('f', CustomLoopLimits.Sha256HexCharacters);
        Assert.NotEqual(persistedJournal.RequestHash, mismatchedRequestHash);
        var corruptedJson = (await File.ReadAllTextAsync(historyPath)).Replace(persistedJournal.RequestHash, mismatchedRequestHash, StringComparison.Ordinal);
        await File.WriteAllTextAsync(historyPath, corruptedJson);

        var cleanup = await port.CleanupAsync(CleanupCommand(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-history-semantic-c"));
        var posture = await port.InspectAsync();

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.Contains(posture.Categories, item => item.Category == CustomLoopReceiptArtifactCategory.Corrupt && item.ArtifactCount > 0);
    }

    [Fact]
    public async Task Corrupt_proof_ledger_is_reported_separately_and_blocks_all_cleanup_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllTextAsync(paths.CustomLoopReceiptProofLedgerPath, "{\"schemaVersion\":99}");

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-corrupt"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
    }

    [Fact]
    public async Task Tombstone_cleanup_preserves_evidence_when_the_delete_receipt_does_not_bind_the_raw_tombstone()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-binding-mismatch");
        await CreateCommittedAsync(store, definition);
        var deletion = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-binding-mismatch", null, definition, definition.DefinitionVersion, _createdAtUtc.AddDays(1));
        var deleted = await store.DeleteAsync(definition.Id, definition.DefinitionVersion, deletion.OperationId, deletion.RequestedAtUtc, deletion);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, deleted.Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(deletion.OperationId));
        var rawTombstone = Assert.IsType<CustomLoopDefinitionTombstone>(deleted.Tombstone);
        var tombstonePath = Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json");
        await File.WriteAllTextAsync(tombstonePath, JsonSerializer.Serialize(rawTombstone with { LastContentHash = new string('0', CustomLoopLimits.Sha256HexCharacters) }, _jsonOptions));

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionTombstone, "cleanup-binding-mismatch"));
        var posture = await store.InspectReceiptRetentionAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone);

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.True(File.Exists(tombstonePath));
    }

    [Fact]
    public async Task Retention_reclaims_recognized_interrupted_atomic_write_under_workspace_ownership()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var staleTemp = Path.Combine(paths.CustomLoopDefinitionOperationsPath, $".{operationId}.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(staleTemp, "{\"interrupted\":true}");

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-stale-temp"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, cleanup.Status);
        Assert.False(File.Exists(staleTemp));
        Assert.Equal(CustomLoopReceiptOperationLookupStatus.Expired, (await store.LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, operationId)).Status);
    }

    [Fact]
    public async Task Retention_fails_closed_on_an_unrecognized_raw_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var unexpectedPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, "unrecognized-artifact.txt");
        await File.WriteAllTextAsync(unexpectedPath, "not-a-receipt");

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-unrecognized-artifact"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.True(File.Exists(unexpectedPath));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
    }

    [Fact]
    public async Task Retention_inventory_fails_closed_on_subdirectories_before_selecting_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var unexpectedDirectory = Path.Combine(paths.CustomLoopDefinitionOperationsPath, "nested");
        Directory.CreateDirectory(unexpectedDirectory);
        await File.WriteAllTextAsync(Path.Combine(unexpectedDirectory, "hidden.json"), "{}");

        var cleanup = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-nested-artifact"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, cleanup.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, cleanup.BlockReason);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
        Assert.True(Directory.Exists(unexpectedDirectory));
    }

    [Fact]
    public async Task Corrupt_tombstone_posture_exposes_corrupt_evidence_instead_of_a_healthy_block_reason()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-corrupt-tombstone-posture");
        await CreateCommittedAsync(store, definition);
        var deletion = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-corrupt-tombstone-posture", null, definition, definition.DefinitionVersion, _createdAtUtc.AddDays(1));
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, (await store.DeleteAsync(definition.Id, definition.DefinitionVersion, deletion.OperationId, deletion.RequestedAtUtc, deletion)).Status);
        var tombstonePath = Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json");
        await File.WriteAllTextAsync(tombstonePath, "{\"schemaVersion\":99}");

        var posture = await store.InspectReceiptRetentionAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone);

        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, posture.CleanupBlockReason);
        Assert.Equal(1, posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Corrupt).ArtifactCount);
    }

    [Fact]
    public async Task Repeated_updates_are_compacted_in_caller_bounded_batches()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-batches");
        await CreateCommittedAsync(store, definition);
        for (var index = 1; index <= CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount + 1; index++)
        {
            var updated = CustomLoopDefinitionContentHash.Apply(definition with
            {
                DefinitionVersion = definition.DefinitionVersion + 1,
                DisplayName = $"Update {index}",
                LastMutationOperationId = $"update-batch-{index}",
                UpdatedAtUtc = _createdAtUtc.AddMinutes(index)
            });
            var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, updated, definition, definition.DefinitionVersion, updated.UpdatedAtUtc);
            Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, (await store.UpdateAsync(updated, definition.DefinitionVersion, mutation)).Status);
            Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));
            definition = updated;
        }

        var first = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-batch-1"));
        var second = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-batch-2"));

        Assert.Equal(CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount, first.CompactedArtifactCount);
        Assert.Equal(1, second.CompactedArtifactCount);
        Assert.Single(Directory.EnumerateFiles(paths.CustomLoopDefinitionOperationsPath, "*.json", SearchOption.TopDirectoryOnly));
        Assert.Equal(definition.ContentHash, (await store.GetAsync(definition.Id))!.ContentHash);
    }

    [Fact]
    public async Task Stale_intent_audited_owner_recovers_when_the_ledger_write_committed_before_the_crash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var artifactPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json");
        var candidate = await CreateCandidateAsync(paths, operationId, new string('a', CustomLoopLimits.Sha256HexCharacters), _createdAtUtc.AddDays(1));
        var staleOwnershipAtUtc = _observedAtUtc.AddMinutes(-2);
        var staleRequest = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-ledger-crash");
        var ledger = ProofLedger(staleOwnershipAtUtc, candidate);
        var journal = CleanupJournal(staleRequest, staleOwnershipAtUtc, CustomLoopReceiptCleanupStage.IntentAuditRecorded, [candidate]);
        await WriteProofLedgerAsync(paths, ledger);
        await WriteCleanupJournalAsync(paths, journal);

        var recovered = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-new"));
        var recoveredLedger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, recovered.Status);
        Assert.NotEqual(journal.OwnerGenerationId, recovered.Journal!.OwnerGenerationId);
        Assert.Equal(staleRequest.OperationId, recovered.Journal.Request.OperationId);
        Assert.Equal(2, recoveredLedger.Generation);
        Assert.Equal(CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger), recoveredLedger.PreviousLedgerHash);
        Assert.False(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Future_dated_request_cannot_extend_cleanup_ownership_and_the_interrupted_intent_fails_closed_on_trusted_time()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new BlockingIntentAuditLog();
        var clock = new MutableTimeProvider(_observedAtUtc);
        var store = new CustomLoopDefinitionStore(paths, audit, clock);
        var operationId = await CreateExpiredUpdateAsync(store);
        var futureRequestedAtUtc = _observedAtUtc.AddDays(7);
        var futureRequest = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-future-request") with
        {
            RequestedAtUtc = futureRequestedAtUtc,
            ReplayCutoffUtc = CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(futureRequestedAtUtc)
        };
        using var cancellation = new CancellationTokenSource();
        var interrupted = store.CleanupReceiptRetentionAsync(futureRequest, cancellation.Token);
        await audit.IntentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var durableIntent = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(await File.ReadAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath));

        Assert.Equal(_observedAtUtc, durableIntent.OwnershipAcquiredAtUtc);
        Assert.True(durableIntent.OwnershipAcquiredAtUtc < durableIntent.Request.RequestedAtUtc);
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, durableIntent.Stage);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        clock.Advance(CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1));
        var recovered = await new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), clock).CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-after-future-crash"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(clock.GetUtcNow(), recovered.Journal!.OwnershipAcquiredAtUtc);
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
    }

    [Fact]
    public async Task Recovery_after_durable_intent_audit_append_does_not_duplicate_the_uncertain_event()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new AppendedIntentThenBlockedAuditLog();
        var clock = new MutableTimeProvider(_observedAtUtc);
        var store = new CustomLoopDefinitionStore(paths, audit, clock);
        var operationId = await CreateExpiredUpdateAsync(store);
        var request = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-intent-audit-crash");
        using var cancellation = new CancellationTokenSource();

        var interrupted = store.CleanupReceiptRetentionAsync(request, cancellation.Token);
        await audit.IntentAppended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(await File.ReadAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath));
        Assert.Equal(CustomLoopReceiptCleanupStage.IntentAuditStarted, started.Stage);
        Assert.Single(audit.Events);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        clock.Advance(CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromSeconds(1));
        var recovered = await new CustomLoopDefinitionStore(paths, audit, clock).CleanupReceiptRetentionAsync(request);

        Assert.Equal(CustomLoopReceiptCleanupStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.Equal(1, audit.Events.Count(item => item.Action == AuditSchema.Actions.LoopDefinitionReceiptRetentionIntent));
        Assert.True(File.Exists(Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json")));
    }

    [Fact]
    public async Task Recovery_after_interrupted_canonical_prefix_removal_reconstructs_progress_and_completes_the_batch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var mutations = await CreateExpiredUpdatesAsync(store, "loop-partial-removal", "update-partial-removal", 2);
        var candidates = new List<CustomLoopReceiptCleanupCandidate>();
        foreach (var mutation in mutations)
        {
            candidates.Add(await CreateCandidateAsync(paths, mutation.OperationId, mutation.RequestHash, mutation.RequestedAtUtc));
        }

        var staleOwnershipAtUtc = _observedAtUtc.AddMinutes(-2);
        var ledger = ProofLedger(staleOwnershipAtUtc, [.. candidates]);
        var request = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-partial-removal-crash");
        var journal = CleanupJournal(request, staleOwnershipAtUtc, CustomLoopReceiptCleanupStage.ProofLedgerWritten, [.. candidates], CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger));
        await WriteProofLedgerAsync(paths, ledger);
        await WriteCleanupJournalAsync(paths, journal);
        var removedPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, candidates[0].ArtifactId + ".json");
        var preservedPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, candidates[1].ArtifactId + ".json");
        File.Delete(removedPath);

        var recovered = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-after-partial-removal"));
        var retainedLedger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.None, recovered.BlockReason);
        Assert.Equal(2, recovered.CompactedArtifactCount);
        Assert.Equal(2, recovered.Journal!.RemovedArtifactCount);
        Assert.False(File.Exists(removedPath));
        Assert.False(File.Exists(preservedPath));
        Assert.Equal(2, retainedLedger.ExpiredOperations.Length);
    }

    [Fact]
    public async Task Recovery_fails_closed_when_a_candidate_path_exists_but_cannot_be_read_as_an_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var mutations = await CreateExpiredUpdatesAsync(store, "loop-unreadable-removal", "update-unreadable-removal", 2);
        var candidates = new List<CustomLoopReceiptCleanupCandidate>();
        foreach (var mutation in mutations)
        {
            candidates.Add(await CreateCandidateAsync(paths, mutation.OperationId, mutation.RequestHash, mutation.RequestedAtUtc));
        }

        var staleOwnershipAtUtc = _observedAtUtc.AddMinutes(-2);
        var ledger = ProofLedger(staleOwnershipAtUtc, [.. candidates]);
        var request = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-unreadable-removal-crash");
        var journal = CleanupJournal(request, staleOwnershipAtUtc, CustomLoopReceiptCleanupStage.ProofLedgerWritten, [.. candidates], CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger));
        await WriteProofLedgerAsync(paths, ledger);
        await WriteCleanupJournalAsync(paths, journal);
        var unreadablePath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, candidates[0].ArtifactId + ".json");
        var preservedPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, candidates[1].ArtifactId + ".json");
        File.Delete(unreadablePath);
        Directory.CreateDirectory(unreadablePath);

        var recovered = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-after-unreadable-removal"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, recovered.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.Equal(0, recovered.Journal.RemovedArtifactCount);
        Assert.True(Directory.Exists(unreadablePath));
        Assert.True(File.Exists(preservedPath));
    }

    [Fact]
    public async Task Recovery_never_removes_raw_evidence_when_the_committed_proof_ledger_is_missing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var operationId = await CreateExpiredUpdateAsync(store);
        var artifactPath = Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json");
        var candidate = await CreateCandidateAsync(paths, operationId, new string('a', CustomLoopLimits.Sha256HexCharacters), _createdAtUtc.AddDays(1));
        var staleOwnershipAtUtc = _observedAtUtc.AddMinutes(-2);
        var ledger = ProofLedger(staleOwnershipAtUtc, candidate);
        var request = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-missing-ledger-crash");
        var journal = CleanupJournal(request, staleOwnershipAtUtc, CustomLoopReceiptCleanupStage.ProofLedgerWritten, [candidate], CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger));
        await WriteCleanupJournalAsync(paths, journal);

        var recovered = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-after-missing-ledger"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.Corrupt, recovered.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.CorruptEvidence, recovered.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStage.Degraded, recovered.Journal!.Stage);
        Assert.True(File.Exists(artifactPath));
        Assert.False(File.Exists(paths.CustomLoopReceiptProofLedgerPath));
    }

    [Fact]
    public async Task Same_id_compact_lineage_that_disagrees_with_a_raw_tombstone_is_corrupt_workspace_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-lineage-conflict");
        await CreateCommittedAsync(store, definition);
        var deletedAtUtc = _createdAtUtc.AddDays(1);
        var deletion = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-lineage-conflict", null, definition, 1, deletedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, (await store.DeleteAsync(definition.Id, 1, deletion.OperationId, deletedAtUtc, deletion)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(deletion.OperationId));
        var tombstonePath = Path.Combine(paths.CustomLoopDefinitionTombstonesPath, definition.Id + ".json");
        var tombstoneBytes = await File.ReadAllBytesAsync(tombstonePath);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, (await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionTombstone, "cleanup-lineage-conflict"))).Status);
        var ledger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        var lineage = Assert.Single(ledger.DefinitionLineage);
        var conflictingLineage = lineage with { LastDefinitionHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var deleteProof = Assert.Single(ledger.ExpiredOperations);
        var conflictingProof = deleteProof with { DeleteLineageBindingHash = CustomLoopReceiptRetentionContractCodec.ComputeDeleteLineageBindingHash(deleteProof.RequestHash, deleteProof.OutcomeHash, conflictingLineage) };
        var conflictingLedger = ledger with { DefinitionLineage = [conflictingLineage], ExpiredOperations = [conflictingProof] };
        await WriteProofLedgerAsync(paths, conflictingLedger);
        await File.WriteAllBytesAsync(tombstonePath, tombstoneBytes);

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));

        Assert.Contains("conflicts with its retained compact proof", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Routine_workspace_reads_reject_role_changed_compact_lineage_when_the_authoritative_delete_receipt_remains()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));
        var definition = CreateDefinition("loop-role-lineage-conflict");
        await CreateCommittedAsync(store, definition);
        var deletedAtUtc = _createdAtUtc.AddDays(1);
        var deletion = Mutation(CustomLoopDefinitionMutationKind.Delete, "delete-role-lineage-conflict", null, definition, 1, deletedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Deleted, (await store.DeleteAsync(definition.Id, 1, deletion.OperationId, deletedAtUtc, deletion)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(deletion.OperationId));
        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, (await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionTombstone, "cleanup-role-lineage-conflict"))).Status);
        var ledger = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(await File.ReadAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath));
        var changedLineage = Assert.Single(ledger.DefinitionLineage) with { RoleId = "different-role" };
        var deleteProof = Assert.Single(ledger.ExpiredOperations);
        var changedProof = deleteProof with { DeleteLineageBindingHash = CustomLoopReceiptRetentionContractCodec.ComputeDeleteLineageBindingHash(deleteProof.RequestHash, deleteProof.OutcomeHash, changedLineage) };
        await WriteProofLedgerAsync(paths, ledger with { DefinitionLineage = [changedLineage], ExpiredOperations = [changedProof] });

        var getException = await Assert.ThrowsAsync<FormatException>(() => store.GetAsync(definition.Id));
        var listException = await Assert.ThrowsAsync<FormatException>(() => store.ListAsync());

        Assert.Contains("conflicts with its retained compact proof", getException.Message, StringComparison.Ordinal);
        Assert.Contains("conflicts with its retained compact proof", listException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_process_mutation_lease_blocks_cleanup_before_it_selects_or_mutates_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var lockPath = Path.Combine(paths.LoopDefinitionsPath, ".custom-loop-mutations.lock");
        using var externalOwnership = new WindowsFileLock(lockPath);
        var store = new CustomLoopDefinitionStore(paths, new RecordingAuditLog(), new FixedTimeProvider(_observedAtUtc));

        var blocked = await store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-external-owner"));

        Assert.Equal(CustomLoopReceiptCleanupStatus.OperationInProgress, blocked.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, blocked.BlockReason);
        Assert.False(File.Exists(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath));
    }

    [Fact]
    public async Task Concurrent_cleanup_reports_owned_window_without_selecting_or_mutating_a_second_batch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new BlockingIntentAuditLog();
        var store = new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc));
        await CreateExpiredUpdateAsync(store);
        var firstTask = store.CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-owner"));
        await audit.IntentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var competing = await new CustomLoopDefinitionStore(paths, audit, new FixedTimeProvider(_observedAtUtc)).CleanupReceiptRetentionAsync(Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "cleanup-competing"));
        audit.ReleaseIntent.TrySetResult();
        var first = await firstTask;

        Assert.Equal(CustomLoopReceiptCleanupStatus.OperationInProgress, competing.Status);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, competing.BlockReason);
        Assert.Equal(CustomLoopReceiptCleanupStatus.Pruned, first.Status);
    }

    private static async Task<IReadOnlyList<CustomLoopDefinitionMutationRequest>> CreateExpiredUpdatesAsync(CustomLoopDefinitionStore store, string loopId, string operationPrefix, int count)
    {
        var definition = CreateDefinition(loopId);
        await CreateCommittedAsync(store, definition);
        var mutations = new List<CustomLoopDefinitionMutationRequest>(count);
        for (var index = 1; index <= count; index++)
        {
            var updatedAtUtc = _createdAtUtc.AddDays(1).AddMinutes(index);
            var updated = CustomLoopDefinitionContentHash.Apply(definition with
            {
                DefinitionVersion = definition.DefinitionVersion + 1,
                DisplayName = $"Update {index}",
                LastMutationOperationId = $"{operationPrefix}-{index}",
                UpdatedAtUtc = updatedAtUtc
            });
            var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, updated, definition, definition.DefinitionVersion, updatedAtUtc);
            Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, (await store.UpdateAsync(updated, definition.DefinitionVersion, mutation)).Status);
            Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));
            mutations.Add(mutation);
            definition = updated;
        }

        return mutations;
    }

    private static async Task<CustomLoopReceiptCleanupCandidate> CreateCandidateAsync(WorkspacePaths paths, string operationId, string requestHash, DateTimeOffset completedAtUtc)
    {
        var path = Path.Combine(paths.CustomLoopDefinitionOperationsPath, operationId + ".json");
        var bytes = await File.ReadAllBytesAsync(path);
        var artifactHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var proof = new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            CustomLoopDefinitionMutationKind.Update,
            CustomLoopDefinitionStoreStatus.Updated,
            null,
            operationId,
            requestHash,
            artifactHash,
            completedAtUtc,
            completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
        return new CustomLoopReceiptCleanupCandidate(operationId, artifactHash, bytes.LongLength, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, null);
    }

    private static CustomLoopReceiptProofLedger ProofLedger(DateTimeOffset createdAtUtc, params CustomLoopReceiptCleanupCandidate[] candidates)
    {
        return new CustomLoopReceiptProofLedger(
            CustomLoopReceiptProofLedger.CurrentSchemaVersion,
            1,
            createdAtUtc,
            null,
            candidates.Where(item => item.DefinitionLineageProof is not null).Select(item => item.DefinitionLineageProof!).ToImmutableArray(),
            candidates.Select(item => item.ExpiredOperationProof!).ToImmutableArray());
    }

    private static CustomLoopReceiptCleanupJournal CleanupJournal(CustomLoopReceiptCleanupRequest request, DateTimeOffset ownershipAcquiredAtUtc, CustomLoopReceiptCleanupStage stage, ImmutableArray<CustomLoopReceiptCleanupCandidate> candidates, string? proofLedgerHash = null)
    {
        return new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-crashed",
            Environment.ProcessId,
            ownershipAcquiredAtUtc,
            stage,
            CustomLoopReceiptCleanupOutcome.Unknown,
            ownershipAcquiredAtUtc,
            candidates,
            proofLedgerHash,
            0,
            0,
            "The prior owner stopped at a deterministic crash boundary.");
    }

    private static async Task WriteProofLedgerAsync(WorkspacePaths paths, CustomLoopReceiptProofLedger ledger)
    {
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger));
    }

    private static async Task WriteCleanupJournalAsync(WorkspacePaths paths, CustomLoopReceiptCleanupJournal journal)
    {
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath, CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));
    }

    private static async Task<string> CreateExpiredUpdateAsync(CustomLoopDefinitionStore store)
    {
        var original = CreateDefinition("loop-expired");
        await CreateCommittedAsync(store, original);
        var updated = Advance(original, "update-expired", _createdAtUtc.AddDays(1));
        var mutation = Mutation(CustomLoopDefinitionMutationKind.Update, updated.LastMutationOperationId, updated, original, 1, updated.UpdatedAtUtc);
        Assert.Equal(CustomLoopDefinitionStoreStatus.Updated, (await store.UpdateAsync(updated, 1, mutation)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(mutation.OperationId));
        return mutation.OperationId;
    }

    private static CustomLoopDefinition CreateDefinition(string id) => CustomLoopDefinition.CreateSeed(id, "default-assistant", $"{id}-step", $"create-{id}", _createdAtUtc);

    private static CustomLoopDefinition Advance(CustomLoopDefinition definition, string operationId, DateTimeOffset updatedAtUtc)
    {
        return CustomLoopDefinitionContentHash.Apply(definition with
        {
            DefinitionVersion = definition.DefinitionVersion + 1,
            DisplayName = "Updated",
            LastMutationOperationId = operationId,
            UpdatedAtUtc = updatedAtUtc
        });
    }

    private static CustomLoopDefinitionMutationRequest Mutation(CustomLoopDefinitionMutationKind kind, string operationId, CustomLoopDefinition? planned, CustomLoopDefinition prior, int expectedVersion, DateTimeOffset requestedAtUtc)
    {
        return new CustomLoopDefinitionMutationRequest(kind, operationId, new string(kind == CustomLoopDefinitionMutationKind.Update ? 'a' : 'b', CustomLoopLimits.Sha256HexCharacters), prior.Id, prior.RoleId, expectedVersion, planned, prior, requestedAtUtc);
    }

    private static CustomLoopReceiptCleanupRequest Request(CustomLoopReceiptArtifactClass artifactClass, string operationId)
    {
        return new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            artifactClass,
            operationId,
            "embodysense.web",
            "web",
            _observedAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(_observedAtUtc),
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount,
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes);
    }

    private static CustomLoopReceiptCleanupCommand CleanupCommand(CustomLoopReceiptArtifactClass artifactClass, string operationId)
    {
        return new CustomLoopReceiptCleanupCommand(
            CustomLoopReceiptCleanupCommand.CurrentSchemaVersion,
            artifactClass,
            operationId,
            "embodysense.web",
            "web",
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount,
            CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes);
    }

    private static async Task CreateCommittedAsync(CustomLoopDefinitionStore store, CustomLoopDefinition definition)
    {
        Assert.Equal(CustomLoopDefinitionStoreStatus.Created, (await store.CreateAsync(definition)).Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, await store.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class MutableTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        private DateTimeOffset _timestamp = timestamp;

        public override DateTimeOffset GetUtcNow() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];
        public int Attempts { get; private set; }
        public int? FailOnAppend { get; init; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == FailOnAppend)
            {
                throw new IOException("audit unavailable");
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class MutatingIntentAuditLog(WorkspacePaths paths) : IAuditLog
    {
        private bool _mutated;

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (!_mutated && auditEvent.Action == AuditSchema.Actions.LoopDefinitionReceiptRetentionIntent)
            {
                _mutated = true;
                var path = Directory.EnumerateFiles(paths.CustomLoopDefinitionOperationsPath, "update-*.json", SearchOption.TopDirectoryOnly).Single();
                await File.AppendAllTextAsync(path, " ", cancellationToken);
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class BlockingIntentAuditLog : IAuditLog
    {
        public TaskCompletionSource IntentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIntent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopDefinitionReceiptRetentionIntent)
            {
                IntentEntered.TrySetResult();
                await ReleaseIntent.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class AppendedIntentThenBlockedAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];
        public TaskCompletionSource IntentAppended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIntent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            if (auditEvent.Action == AuditSchema.Actions.LoopDefinitionReceiptRetentionIntent)
            {
                IntentAppended.TrySetResult();
                await ReleaseIntent.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }
}
