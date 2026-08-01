using System.Collections.Immutable;
using System.Text;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Tests.Loops.ReceiptRetention;

public sealed class CustomLoopReceiptRetentionContractTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Cleanup_request_enforces_schema_identity_horizon_and_batch_boundaries()
    {
        var valid = Request(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount, CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes);

        CustomLoopReceiptRetentionContractValidator.ValidateCleanupRequest(valid);
        Assert.All(
        [
            valid with { SchemaVersion = 2 },
            valid with { ArtifactClass = CustomLoopReceiptArtifactClass.Unknown },
            valid with { OperationId = "Unsafe" },
            valid with { Actor = "unsafe\nactor" },
            valid with { Surface = "Unsafe" },
            valid with { RequestedAtUtc = valid.RequestedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ReplayCutoffUtc = valid.ReplayCutoffUtc.AddTicks(1) },
            valid with { MaximumArtifactCount = 0 },
            valid with { MaximumArtifactCount = CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount + 1 },
            valid with { MaximumArtifactUtf8Bytes = 0 },
            valid with { MaximumArtifactUtf8Bytes = CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes + 1 }
        ], candidate => Assert.ThrowsAny<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupRequest(candidate)));
    }

    [Fact]
    public void Proof_contracts_preserve_exact_expiry_fingerprints_and_deleted_identity_lineage()
    {
        var operation = ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "mutation-old");
        var lineage = Lineage("loop-old");

        CustomLoopReceiptRetentionContractValidator.ValidateExpiredOperationProof(operation);
        CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(lineage);
        Assert.InRange(CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes(operation), 1, (int)CustomLoopReceiptRetentionPolicy.MaxDefinitionMutationProofUtf8Bytes);
        Assert.InRange(CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8Bytes(lineage), 1, (int)CustomLoopReceiptRetentionPolicy.MaxDefinitionLineageProofUtf8Bytes);
        Assert.All(
        [
            operation with { SchemaVersion = 2 },
            operation with { ArtifactClass = CustomLoopReceiptArtifactClass.DefinitionTombstone },
            operation with { OperationId = "Unsafe" },
            operation with { RequestHash = HashA[..63] },
            operation with { OutcomeHash = HashA.ToUpperInvariant() },
            operation with { ExpiredAtUtc = operation.ExpiredAtUtc.AddTicks(1) }
        ], candidate => Assert.ThrowsAny<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateExpiredOperationProof(candidate)));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(lineage with { DeletedAtUtc = null }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(lineage with { IsDeleted = false }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(lineage with { LastDefinitionVersion = 0 }));
    }

    [Fact]
    public void Proof_ledger_serialization_hash_and_equality_are_order_independent_and_strict()
    {
        var first = Ledger(
            [Lineage("loop-b"), Lineage("loop-a")],
            [ExpiredProof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt, "control-b"), ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "mutation-a")]);
        var reordered = first with
        {
            DefinitionLineage = first.DefinitionLineage.Reverse().ToImmutableArray(),
            ExpiredOperations = first.ExpiredOperations.Reverse().ToImmutableArray()
        };

        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(first);
        var roundTrip = CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(bytes);

        Assert.True(CustomLoopReceiptRetentionContractCodec.ProofLedgersEqual(first, reordered));
        Assert.Equal(CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(first), CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(reordered));
        Assert.True(CustomLoopReceiptRetentionContractCodec.ProofLedgersEqual(first, roundTrip));
        Assert.Matches("^[0-9a-f]{64}$", CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(first));

        var withUnknownField = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)[..^1] + ",\"legacy\":true}");
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(withUnknownField));
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(ReadOnlySpan<byte>.Empty));
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger("null"u8));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(first with { Generation = 2 }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(first with { PreviousLedgerHash = HashA }));
        var futureProof = ExpiredProof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt, "future-proof") with { CompletedAtUtc = _now, ExpiredAtUtc = _now.AddDays(30) };
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(first with { ExpiredOperations = first.ExpiredOperations.Append(futureProof).ToImmutableArray() }));
    }

    [Fact]
    public void Proof_ledger_rejects_duplicates_and_class_count_overflow()
    {
        var duplicateProof = ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "mutation-a");
        var duplicate = Ledger([], [duplicateProof]) with { ExpiredOperations = [duplicateProof, duplicateProof] };
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(duplicate));

        var tooMany = Enumerable.Range(0, CustomLoopReceiptRetentionPolicy.MaxDefinitionMutationProofCount + 1)
            .Select(index => ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, $"mutation-{index}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(Ledger([], tooMany)));

        var tooManyLineages = Enumerable.Range(0, CustomLoopReceiptRetentionPolicy.MaxDefinitionLineageProofCount + 1)
            .Select(index => Lineage($"loop-{index}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(Ledger(tooManyLineages, [])));
    }

    [Theory]
    [InlineData(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)]
    [InlineData(CustomLoopReceiptArtifactClass.DefinitionTombstone)]
    [InlineData(CustomLoopReceiptArtifactClass.LifecycleControlReceipt)]
    public void Proof_accounting_enforces_each_class_count_and_byte_ceiling(CustomLoopReceiptArtifactClass artifactClass)
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);

        CustomLoopReceiptRetentionContractValidator.ValidateProofAccounting(artifactClass, budget.MaximumProofCount, budget.MaximumProofUtf8Bytes);
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofAccounting(artifactClass, budget.MaximumProofCount + 1, budget.MaximumProofUtf8Bytes));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateProofAccounting(artifactClass, budget.MaximumProofCount, budget.MaximumProofUtf8Bytes + 1));
    }

    [Fact]
    public void Lookup_contract_distinguishes_exact_expired_and_unknown_without_reuse_ambiguity()
    {
        var proof = ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "mutation-old");
        var exact = new CustomLoopReceiptOperationLookupResult(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, "mutation-live", CustomLoopReceiptOperationLookupStatus.Exact, null, "Exact receipt retained.");
        var expired = new CustomLoopReceiptOperationLookupResult(CustomLoopReceiptArtifactClass.DefinitionTombstone, proof.OperationId, CustomLoopReceiptOperationLookupStatus.Expired, proof, "Exact replay expired; operation identity remains reserved.");
        var unknown = new CustomLoopReceiptOperationLookupResult(CustomLoopReceiptArtifactClass.LifecycleControlReceipt, "control-new", CustomLoopReceiptOperationLookupStatus.Unknown, null, "Operation identity is unknown.");

        CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(exact);
        CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(expired);
        CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(unknown);
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(expired with { ExpiredProof = null }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(unknown with { ExpiredProof = proof }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(exact with { Status = CustomLoopReceiptOperationLookupStatus.UnknownStatus }));
    }

    [Fact]
    public void Cleanup_candidates_fail_closed_for_every_unsafe_category_and_unresolved_evidence()
    {
        var valid = Journal(CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptArtifactClass.LifecycleControlReceipt);
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid);

        foreach (var category in Enum.GetValues<CustomLoopReceiptArtifactCategory>().Where(item => item != CustomLoopReceiptArtifactCategory.Compactable))
        {
            var candidate = valid.Candidates[0] with { Category = category };
            Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid with { Candidates = [candidate] }));
        }

        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid with { Candidates = [valid.Candidates[0] with { OutcomeAuditRecorded = false }] }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid with { Candidates = [valid.Candidates[0] with { OwnershipResolved = false }] }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid with { Candidates = [valid.Candidates[0] with { ArtifactId = "different-operation" }] }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(valid with { Candidates = [valid.Candidates[0] with { ExpiredOperationProof = valid.Candidates[0].ExpiredOperationProof! with { CompletedAtUtc = valid.Request.ReplayCutoffUtc.AddTicks(1), ExpiredAtUtc = valid.Request.RequestedAtUtc.AddTicks(1) } }] }));

        var mutation = Journal(CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var unrelatedLineage = Lineage("unrelated-loop");
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(mutation with { Candidates = [mutation.Candidates[0] with { DefinitionLineageProof = unrelatedLineage }] }));

        var tombstone = Journal(CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptArtifactClass.DefinitionTombstone);
        var futureLineage = tombstone.Candidates[0].DefinitionLineageProof! with { DeletedAtUtc = tombstone.Candidates[0].ExpiredOperationProof!.CompletedAtUtc.AddTicks(1) };
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(tombstone with { Candidates = [tombstone.Candidates[0] with { DefinitionLineageProof = futureLineage }] }));
    }

    [Fact]
    public void Cleanup_journal_ownership_chronology_is_independent_from_the_caller_request_timestamp()
    {
        var journal = Journal(CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var futureRequest = journal.Request with
        {
            RequestedAtUtc = journal.Request.RequestedAtUtc.AddDays(1),
            ReplayCutoffUtc = CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(journal.Request.RequestedAtUtc.AddDays(1))
        };
        journal = journal with
        {
            Request = futureRequest,
            RequestHash = CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(futureRequest)
        };

        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);

        Assert.True(journal.OwnershipAcquiredAtUtc < journal.Request.RequestedAtUtc);
    }

    [Theory]
    [InlineData(CustomLoopReceiptCleanupStage.IntentPersisted)]
    [InlineData(CustomLoopReceiptCleanupStage.IntentAuditRecorded)]
    [InlineData(CustomLoopReceiptCleanupStage.ProofLedgerWritten)]
    [InlineData(CustomLoopReceiptCleanupStage.ArtifactsRemoved)]
    [InlineData(CustomLoopReceiptCleanupStage.OutcomeAuditStarted)]
    [InlineData(CustomLoopReceiptCleanupStage.Completed)]
    [InlineData(CustomLoopReceiptCleanupStage.CommittedWithAuditWarning)]
    [InlineData(CustomLoopReceiptCleanupStage.AbandonedConflict)]
    [InlineData(CustomLoopReceiptCleanupStage.Degraded)]
    public void Cleanup_journal_models_every_recoverable_and_fail_closed_stage(CustomLoopReceiptCleanupStage stage)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(Journal(stage, CustomLoopReceiptArtifactClass.DefinitionTombstone));
    }

    [Fact]
    public void Cleanup_journal_hash_equality_roundtrip_and_state_accounting_are_deterministic()
    {
        var journal = Journal(CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, candidateCount: 2);
        var reordered = journal with { Candidates = journal.Candidates.Reverse().ToImmutableArray() };
        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal);
        var roundTrip = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(bytes);

        Assert.True(CustomLoopReceiptRetentionContractCodec.CleanupJournalsEqual(journal, reordered));
        Assert.Equal(CustomLoopReceiptRetentionContractCodec.ComputeCleanupJournalHash(journal), CustomLoopReceiptRetentionContractCodec.ComputeCleanupJournalHash(reordered));
        Assert.True(CustomLoopReceiptRetentionContractCodec.CleanupJournalsEqual(journal, roundTrip));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal with { RequestHash = HashC }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal with { RemovedArtifactCount = 0 }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal with { Outcome = CustomLoopReceiptCleanupOutcome.Conflict }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal with { UpdatedAtUtc = journal.OwnershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow + TimeSpan.FromTicks(1) }));
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(Journal(CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptArtifactClass.LifecycleControlReceipt) with { ProofLedgerHash = HashC });
        var degradedAfterRemoval = journal with { Stage = CustomLoopReceiptCleanupStage.Degraded, Outcome = CustomLoopReceiptCleanupOutcome.Degraded };
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(degradedAfterRemoval);
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(degradedAfterRemoval with { ProofLedgerHash = null }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(degradedAfterRemoval with { RemovedArtifactCount = 1 }));
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal("null"u8));
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal("{"u8));
        Assert.Throws<FormatException>(() => CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(new byte[checked((int)CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes + 1)]));
    }

    [Fact]
    public void Empty_completed_journal_records_nothing_eligible_without_claiming_proof_or_removal()
    {
        var journal = Journal(CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, candidateCount: 0);

        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);
        Assert.Equal(CustomLoopReceiptCleanupOutcome.NothingEligible, journal.Outcome);
        Assert.Null(journal.ProofLedgerHash);
        Assert.Equal(0, journal.RemovedArtifactCount);
    }

    [Fact]
    public void Class_and_workspace_posture_account_every_category_and_require_actionable_exhaustion()
    {
        var mutation = Posture(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var tombstone = Posture(CustomLoopReceiptArtifactClass.DefinitionTombstone);
        var control = Posture(CustomLoopReceiptArtifactClass.LifecycleControlReceipt);
        var workspace = new CustomLoopReceiptRetentionPosture(_now, [mutation, tombstone, control], 100, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, "Healthy bounded receipt retention.");

        CustomLoopReceiptRetentionContractValidator.ValidateWorkspacePosture(workspace);
        Assert.Equal(3, mutation.ArtifactCount);
        Assert.Equal(30, mutation.ArtifactUtf8Bytes);
        Assert.Equal(2, mutation.ProofCount);
        Assert.Equal(6, mutation.ProofUtf8Bytes);
        Assert.Equal(mutation.AccountedUtf8Bytes + tombstone.AccountedUtf8Bytes + control.AccountedUtf8Bytes + 100, workspace.AccountedWorkspaceUtf8Bytes);
        Assert.Equal(workspace.MaximumWorkspaceUtf8Bytes - workspace.AccountedWorkspaceUtf8Bytes, workspace.AvailableWorkspaceUtf8Bytes);
        Assert.False(mutation.IsExhausted);
        Assert.False(mutation.IsCleanupBlocked);

        var mutationUsage = Usage(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var overLimitUsage = mutationUsage.SetItem(0, mutationUsage[0] with { ArtifactCount = mutation.Budget.MaximumArtifactCount + 1 });
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { Categories = overLimitUsage }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { Categories = mutation.Categories.RemoveAt(mutation.Categories.Length - 1) }));
        var retainedLineageIndex = mutation.Categories.IndexOf(mutation.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.RetainedLineage));
        var incompatibleProof = mutation.Categories.SetItem(retainedLineageIndex, new CustomLoopReceiptCategoryUsage(CustomLoopReceiptArtifactCategory.RetainedLineage, 1, 5));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { Categories = incompatibleProof }));
        var liveIndex = mutation.Categories.IndexOf(mutation.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Live));
        var inconsistentUsage = mutation.Categories.SetItem(liveIndex, new CustomLoopReceiptCategoryUsage(CustomLoopReceiptArtifactCategory.Live, 0, 1));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { Categories = inconsistentUsage }));
        var noLiveUsage = mutation.Categories.SetItem(liveIndex, new CustomLoopReceiptCategoryUsage(CustomLoopReceiptArtifactCategory.Live, 0, 0));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { Categories = noLiveUsage }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(mutation with { OldestExactReplayExpiresAtUtc = null, NewestExactReplayExpiresAtUtc = null }));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionContractValidator.ValidateWorkspacePosture(workspace with { Classes = [mutation, tombstone, tombstone] }));
    }

    [Theory]
    [InlineData(CustomLoopReceiptCleanupStatus.Pruned, true)]
    [InlineData(CustomLoopReceiptCleanupStatus.Replayed, true)]
    [InlineData(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, true)]
    [InlineData(CustomLoopReceiptCleanupStatus.NothingEligible, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.QuotaExhausted, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.AuditUnavailable, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.CleanupConflict, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.Corrupt, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.Degraded, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.OperationInProgress, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.Invalid, false)]
    [InlineData(CustomLoopReceiptCleanupStatus.Unknown, false)]
    public void Cleanup_result_exposes_only_committed_outcomes(CustomLoopReceiptCleanupStatus status, bool expected)
    {
        var result = new CustomLoopReceiptCleanupResult(status, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, 0, 0, "Result detail.");

        Assert.Equal(expected, result.IsCommitted);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Journal);
        Assert.Equal(CustomLoopReceiptQuotaExhaustionReason.None, result.ExhaustionReason);
        Assert.Equal(CustomLoopReceiptCleanupBlockReason.None, result.BlockReason);
        Assert.Equal(0, result.CompactedArtifactCount);
        Assert.Equal(0, result.CompactedArtifactUtf8Bytes);
        Assert.Equal("Result detail.", result.Detail);
    }

    [Fact]
    public void Application_retention_port_keeps_concrete_adapters_out_of_the_contract_assembly()
    {
        var references = typeof(ICustomLoopReceiptRetentionPort).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.DoesNotContain("EmbodySense.Core.Persistence", references);
        Assert.DoesNotContain("EmbodySense.Core.Clients", references);
        Assert.DoesNotContain("EmbodySense.Core.Startup", references);
        Assert.Equal(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, new FakePort().ArtifactClass);
    }

    private static CustomLoopReceiptCleanupRequest Request(CustomLoopReceiptArtifactClass artifactClass, int maximumCount = 2, long maximumBytes = 4_096)
    {
        return new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            artifactClass,
            $"cleanup-{artifactClass.ToString().ToLowerInvariant()}",
            "embodysense.web",
            "web",
            _now,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(_now),
            maximumCount,
            maximumBytes);
    }

    private static CustomLoopExpiredOperationProof ExpiredProof(CustomLoopReceiptArtifactClass artifactClass, string operationId)
    {
        var completedAtUtc = _now - CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        return new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, artifactClass, operationId, HashA, HashB, completedAtUtc, _now);
    }

    private static CustomLoopDefinitionLineageProof Lineage(string loopId)
    {
        return new CustomLoopDefinitionLineageProof(CustomLoopDefinitionLineageProof.CurrentSchemaVersion, loopId, "role-primary", 3, HashC, $"delete-{loopId}", true, _now.AddDays(-31));
    }

    private static CustomLoopReceiptProofLedger Ledger(CustomLoopDefinitionLineageProof[] lineage, CustomLoopExpiredOperationProof[] operations)
    {
        var linkedLineageOperations = lineage
            .Where(item => item.IsDeleted)
            .Select(item => ExpiredProof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, item.LastMutationOperationId));
        var allOperations = operations
            .Concat(linkedLineageOperations)
            .GroupBy(item => (item.ArtifactClass, item.OperationId))
            .Select(group => group.First())
            .ToArray();
        return new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, 1, _now, null, lineage.ToImmutableArray(), allOperations.ToImmutableArray());
    }

    private static CustomLoopReceiptCleanupJournal Journal(CustomLoopReceiptCleanupStage stage, CustomLoopReceiptArtifactClass artifactClass, int candidateCount = 1)
    {
        var request = Request(artifactClass, Math.Max(1, candidateCount));
        var candidates = Enumerable.Range(0, candidateCount).Select(index => Candidate(artifactClass, $"artifact-{index}")).ToImmutableArray();
        var proofRequired = stage is CustomLoopReceiptCleanupStage.ProofLedgerWritten
            or CustomLoopReceiptCleanupStage.ArtifactsRemoved
            or CustomLoopReceiptCleanupStage.OutcomeAuditStarted
            or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning
            || stage == CustomLoopReceiptCleanupStage.Completed && candidateCount > 0;
        var removalCommitted = stage is CustomLoopReceiptCleanupStage.ArtifactsRemoved
            or CustomLoopReceiptCleanupStage.OutcomeAuditStarted
            or CustomLoopReceiptCleanupStage.Completed
            or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning;
        var outcome = stage switch
        {
            CustomLoopReceiptCleanupStage.Completed => candidateCount == 0 ? CustomLoopReceiptCleanupOutcome.NothingEligible : CustomLoopReceiptCleanupOutcome.Succeeded,
            CustomLoopReceiptCleanupStage.CommittedWithAuditWarning => CustomLoopReceiptCleanupOutcome.AuditUnavailable,
            CustomLoopReceiptCleanupStage.AbandonedConflict => CustomLoopReceiptCleanupOutcome.Conflict,
            CustomLoopReceiptCleanupStage.Degraded => CustomLoopReceiptCleanupOutcome.Degraded,
            _ => CustomLoopReceiptCleanupOutcome.Unknown
        };
        return new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "owner-generation",
            123,
            _now,
            stage,
            outcome,
            _now,
            candidates,
            proofRequired ? HashC : null,
            removalCommitted ? candidates.Length : 0,
            removalCommitted ? candidates.Sum(item => item.ArtifactUtf8Bytes) : 0,
            "Bounded cleanup journal.");
    }

    private static CustomLoopReceiptCleanupCandidate Candidate(CustomLoopReceiptArtifactClass artifactClass, string artifactId)
    {
        var operationClass = artifactClass == CustomLoopReceiptArtifactClass.DefinitionTombstone
            ? CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
            : artifactClass;
        var lineage = artifactClass == CustomLoopReceiptArtifactClass.DefinitionTombstone ? Lineage(artifactId) : null;
        var operationId = lineage?.LastMutationOperationId ?? artifactId;
        return new CustomLoopReceiptCleanupCandidate(
            artifactId,
            HashA,
            100,
            CustomLoopReceiptArtifactCategory.Compactable,
            true,
            true,
            ExpiredProof(operationClass, operationId),
            lineage);
    }

    private static CustomLoopReceiptClassPosture Posture(CustomLoopReceiptArtifactClass artifactClass)
    {
        return new CustomLoopReceiptClassPosture(
            artifactClass,
            CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass),
            Usage(artifactClass),
            _now.AddDays(1),
            _now.AddDays(2),
            CustomLoopReceiptQuotaExhaustionReason.None,
            CustomLoopReceiptCleanupBlockReason.None,
            "Bounded class posture.");
    }

    private static ImmutableArray<CustomLoopReceiptCategoryUsage> Usage(CustomLoopReceiptArtifactClass artifactClass)
    {
        return Enum.GetValues<CustomLoopReceiptArtifactCategory>()
            .Where(item => item != CustomLoopReceiptArtifactCategory.Unknown)
            .Select(item => item switch
            {
                CustomLoopReceiptArtifactCategory.Live => new CustomLoopReceiptCategoryUsage(item, 2, 20),
                CustomLoopReceiptArtifactCategory.Pending => new CustomLoopReceiptCategoryUsage(item, 1, 10),
                CustomLoopReceiptArtifactCategory.RetainedLineage when artifactClass == CustomLoopReceiptArtifactClass.DefinitionTombstone => new CustomLoopReceiptCategoryUsage(item, 1, 5),
                CustomLoopReceiptArtifactCategory.ExpiredIdempotency when artifactClass != CustomLoopReceiptArtifactClass.DefinitionTombstone => new CustomLoopReceiptCategoryUsage(item, 2, 6),
                _ => new CustomLoopReceiptCategoryUsage(item, 0, 0)
            })
            .ToImmutableArray();
    }

    private sealed class FakePort : ICustomLoopReceiptRetentionPort
    {
        public CustomLoopReceiptArtifactClass ArtifactClass => CustomLoopReceiptArtifactClass.DefinitionMutationReceipt;

        public Task<CustomLoopReceiptClassPosture> InspectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopReceiptOperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopReceiptCleanupResult> CleanupAsync(CustomLoopReceiptCleanupRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
