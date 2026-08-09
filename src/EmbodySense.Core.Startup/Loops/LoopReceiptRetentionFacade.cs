using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>
/// Exposes bounded custom-loop receipt-retention posture and explicit cleanup through Core.Startup.
/// </summary>
/// <remarks>
/// The facade composes the existing authoring and lifecycle retention ports; it does not interpret or recreate their
/// persistence protocol. It supplies only server-owned actor and surface values to cleanup, projects safe accounting
/// snapshots, and never schedules or performs cleanup without an explicit caller request.
/// </remarks>
public sealed class LoopReceiptRetentionFacade : ILoopReceiptRetentionFacade
{
    private static readonly CustomLoopReceiptArtifactClass[] _artifactClasses =
    [
        CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
        CustomLoopReceiptArtifactClass.DefinitionTombstone,
        CustomLoopReceiptArtifactClass.LifecycleControlReceipt
    ];

    private readonly IReadOnlyDictionary<CustomLoopReceiptArtifactClass, ICustomLoopReceiptRetentionPort> _ports;
    private readonly CustomLoopDefinitionStore _definitionStore;
    private readonly CustomLoopControlOperationStore _controlStore;
    private readonly string _actor;
    private readonly string _surface;

    /// <summary>
    /// Creates a Web-attributed receipt-retention facade for one workspace.
    /// </summary>
    /// <param name="workingDirectory">The workspace root, normalized to an absolute path.</param>
    public LoopReceiptRetentionFacade(string workingDirectory) : this(workingDirectory, WorkspaceActors.Web, AgentRuntimeSurface.Web.Id)
    {
    }

    /// <summary>
    /// Creates a receipt-retention facade with server-owned audit attribution.
    /// </summary>
    /// <param name="workingDirectory">The workspace root, normalized to an absolute path.</param>
    /// <param name="authenticatedActor">The nonblank authenticated actor written to governed cleanup audit evidence.</param>
    /// <param name="authenticatedSurface">The nonblank owning runtime surface written to governed cleanup audit evidence.</param>
    public LoopReceiptRetentionFacade(string workingDirectory, string authenticatedActor, string authenticatedSurface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedActor);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedSurface);

        var paths = new WorkspacePaths(workingDirectory);
        var audit = new AuditLog(paths);
        _definitionStore = new CustomLoopDefinitionStore(paths, audit);
        _controlStore = new CustomLoopControlOperationStore(paths, audit);
        _ports = new Dictionary<CustomLoopReceiptArtifactClass, ICustomLoopReceiptRetentionPort>
        {
            [CustomLoopReceiptArtifactClass.DefinitionMutationReceipt] = _definitionStore.CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt),
            [CustomLoopReceiptArtifactClass.DefinitionTombstone] = _definitionStore.CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass.DefinitionTombstone),
            [CustomLoopReceiptArtifactClass.LifecycleControlReceipt] = _controlStore
        };
        _actor = authenticatedActor;
        _surface = authenticatedSurface;
    }

    /// <summary>
    /// Inspects safe per-class and workspace-wide receipt-retention posture.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel bounded persistence inspection.</param>
    /// <returns>A complete interface-owned retention posture snapshot.</returns>
    public async Task<LoopReceiptRetentionPostureSnapshot> GetPostureAsync(CancellationToken cancellationToken = default)
    {
        var classes = new List<LoopReceiptRetentionClassSnapshot>(_artifactClasses.Length);
        var accountingCorrupt = false;
        foreach (var artifactClass in _artifactClasses)
        {
            var posture = await InspectClassAsync(artifactClass, cancellationToken);
            try
            {
                var journal = await _ports[artifactClass].InspectActiveCleanupJournalAsync(cancellationToken);
                var journalHealth = MapJournalHealth(journal);
                var combinedHealth = MostSevere(posture.Health, journalHealth);
                var journalPosture = posture with { Health = journalHealth, CleanupBlockReason = BlockReasonForJournal(journal) };
                classes.Add(posture with
                {
                    Health = combinedHealth,
                    ActiveCleanupJournalUtf8Bytes = journal.Utf8Bytes,
                    CleanupRecoveryAvailableAtUtc = journal.RecoveryAvailableAtUtc,
                    CleanupBlockReason = LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([posture, journalPosture]),
                    Detail = journal.Stage is null ? posture.Detail : $"{posture.Detail} Active cleanup journal: {journal.Stage} / {journal.Outcome}."
                });
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                accountingCorrupt = true;
                classes.Add(posture with
                {
                    Health = LoopReceiptRetentionHealth.Corrupt,
                    CleanupBlockReason = CustomLoopReceiptCleanupBlockReason.CorruptEvidence.ToString(),
                    Detail = "Cleanup journal accounting could not be validated safely; cleanup remains unavailable until the evidence is repaired."
                });
            }
        }

        var activeJournalBytes = classes.Sum(item => item.ActiveCleanupJournalUtf8Bytes);
        var accountedBytes = checked(classes.Sum(item => item.ArtifactUtf8Bytes + item.ProofUtf8Bytes + item.CompletedCleanupHistoryUtf8Bytes + item.ActiveCleanupJournalUtf8Bytes));
        var maximumBytes = CustomLoopReceiptRetentionPolicy.MaxAccountedWorkspaceUtf8Bytes;
        var exhausted = accountedBytes >= maximumBytes;
        var health = accountingCorrupt ? LoopReceiptRetentionHealth.Corrupt : classes.Select(item => item.Health).Aggregate(LoopReceiptRetentionHealth.Healthy, MostSevere);
        if (exhausted)
        {
            health = MostSevere(health, LoopReceiptRetentionHealth.Exhausted);
        }

        var exhaustionReason = exhausted
            ? CustomLoopReceiptQuotaExhaustionReason.WorkspaceByteLimit.ToString()
            : classes.Select(item => item.ExhaustionReason).FirstOrDefault(item => !string.Equals(item, nameof(CustomLoopReceiptQuotaExhaustionReason.None), StringComparison.Ordinal)) ?? nameof(CustomLoopReceiptQuotaExhaustionReason.None);
        var blockReason = LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason(classes);
        var detail = accountingCorrupt
            ? "Retention journal accounting could not be validated safely; cleanup remains unavailable until the evidence is repaired."
            : health switch
            {
                LoopReceiptRetentionHealth.Healthy => "Custom-loop receipt retention is within its bounded workspace posture; cleanup remains explicit.",
                LoopReceiptRetentionHealth.Exhausted => "A bounded retention capacity ceiling is exhausted; inspect the affected class before requesting explicit cleanup.",
                LoopReceiptRetentionHealth.RecoveryPending => "A durable cleanup journal is inside its bounded ownership or recovery window; no second cleanup should be started.",
                _ => "Receipt-retention evidence requires review; cleanup will fail closed where the protocol cannot prove safety."
            };

        return new LoopReceiptRetentionPostureSnapshot(
            DateTimeOffset.UtcNow,
            health,
            classes,
            activeJournalBytes,
            accountedBytes,
            maximumBytes,
            Math.Max(0, maximumBytes - accountedBytes),
            exhaustionReason,
            blockReason,
            detail);
    }

    /// <summary>
    /// Executes one explicit, policy-bounded cleanup with server-owned Web audit attribution.
    /// </summary>
    /// <param name="input">The artifact class, caller idempotency identity, and bounded cleanup limits.</param>
    /// <param name="cancellationToken">The token used to cancel cleanup before its durable terminal boundary.</param>
    /// <returns>A safe projection of the durable cleanup outcome.</returns>
    public async Task<LoopReceiptCleanupResponse> CleanupAsync(LoopReceiptCleanupInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TryParseArtifactClass(input.ArtifactClass, out var artifactClass))
        {
            return InvalidCleanup("A supported receipt artifact class is required.");
        }

        var command = new CustomLoopReceiptCleanupCommand(
            CustomLoopReceiptCleanupCommand.CurrentSchemaVersion,
            artifactClass,
            input.OperationId,
            _actor,
            _surface,
            input.MaximumArtifactCount,
            input.MaximumArtifactUtf8Bytes);
        try
        {
            return Map(await _ports[artifactClass].CleanupAsync(command, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new LoopReceiptCleanupResponse(
                CustomLoopReceiptCleanupStatus.Corrupt.ToString(),
                LoopReceiptRetentionHealth.Corrupt,
                false,
                nameof(CustomLoopReceiptQuotaExhaustionReason.None),
                CustomLoopReceiptCleanupBlockReason.CorruptEvidence.ToString(),
                0,
                0,
                "Receipt cleanup could not validate its durable evidence safely; no additional cleanup was attempted.");
        }
    }

    private async Task<LoopReceiptRetentionClassSnapshot> InspectClassAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken)
    {
        try
        {
            return Map(await _ports[artifactClass].InspectAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);
            return new LoopReceiptRetentionClassSnapshot(
                artifactClass.ToString(),
                LoopReceiptRetentionHealth.Corrupt,
                0,
                0,
                budget.MaximumArtifactCount,
                budget.MaximumArtifactUtf8Bytes,
                budget.ReservedPendingCompletionCount,
                budget.ReservedPendingCompletionUtf8Bytes,
                0,
                0,
                budget.MaximumProofCount,
                budget.MaximumProofUtf8Bytes,
                0,
                null,
                0,
                0,
                null,
                null,
                nameof(CustomLoopReceiptQuotaExhaustionReason.None),
                CustomLoopReceiptCleanupBlockReason.CorruptEvidence.ToString(),
                [],
                "Receipt-retention evidence could not be classified safely; cleanup will fail closed.");
        }
    }

    private static LoopReceiptRetentionClassSnapshot Map(CustomLoopReceiptClassPosture posture)
    {
        return new LoopReceiptRetentionClassSnapshot(
            posture.ArtifactClass.ToString(),
            LoopReceiptRetentionHealthProjection.FromPosture(posture.ExhaustionReason.ToString(), posture.CleanupBlockReason.ToString()),
            posture.ArtifactCount,
            posture.ArtifactUtf8Bytes,
            posture.Budget.MaximumArtifactCount,
            posture.Budget.MaximumArtifactUtf8Bytes,
            posture.Budget.ReservedPendingCompletionCount,
            posture.Budget.ReservedPendingCompletionUtf8Bytes,
            posture.ProofCount,
            posture.ProofUtf8Bytes,
            posture.Budget.MaximumProofCount,
            posture.Budget.MaximumProofUtf8Bytes,
            0,
            null,
            posture.CompletedCleanupOperationCount,
            posture.CompletedCleanupHistoryUtf8Bytes,
            posture.OldestExactReplayExpiresAtUtc,
            posture.NewestExactReplayExpiresAtUtc,
            posture.ExhaustionReason.ToString(),
            posture.CleanupBlockReason.ToString(),
            posture.Categories.Select(item => new LoopReceiptCategoryUsageSnapshot(item.Category.ToString(), item.ArtifactCount, item.Utf8Bytes)).ToArray(),
            posture.Detail);
    }

    private static LoopReceiptCleanupResponse Map(CustomLoopReceiptCleanupResult result)
    {
        var health = LoopReceiptRetentionHealthProjection.FromCleanup(result.Status.ToString(), result.ExhaustionReason.ToString(), result.BlockReason.ToString());
        return new LoopReceiptCleanupResponse(
            result.Status.ToString(),
            health,
            result.IsCommitted,
            result.ExhaustionReason.ToString(),
            result.BlockReason.ToString(),
            result.CompactedArtifactCount,
            result.CompactedArtifactUtf8Bytes,
            SafeCleanupDetail(result.Status));
    }

    private static LoopReceiptCleanupResponse InvalidCleanup(string detail)
    {
        return new LoopReceiptCleanupResponse(
            CustomLoopReceiptCleanupStatus.Invalid.ToString(),
            LoopReceiptRetentionHealth.Degraded,
            false,
            nameof(CustomLoopReceiptQuotaExhaustionReason.None),
            nameof(CustomLoopReceiptCleanupBlockReason.None),
            0,
            0,
            detail);
    }

    private static bool TryParseArtifactClass(string value, out CustomLoopReceiptArtifactClass artifactClass)
    {
        artifactClass = CustomLoopReceiptArtifactClass.Unknown;
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var candidate in _artifactClasses)
        {
            if (!string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            artifactClass = candidate;
            return true;
        }

        return false;
    }

    private static string SafeCleanupDetail(CustomLoopReceiptCleanupStatus status)
    {
        return status switch
        {
            CustomLoopReceiptCleanupStatus.Pruned => "Eligible expired receipt evidence was compacted within the requested bounds.",
            CustomLoopReceiptCleanupStatus.Replayed => "The prior terminal cleanup outcome was replayed; no second cleanup was started.",
            CustomLoopReceiptCleanupStatus.NothingEligible => "No eligible expired receipt evidence was available for cleanup.",
            CustomLoopReceiptCleanupStatus.OperationInProgress => "A cleanup owner is inside its bounded ownership window; retry after the displayed recovery time.",
            CustomLoopReceiptCleanupStatus.QuotaExhausted => "Cleanup could not proceed because a bounded retention capacity is exhausted.",
            CustomLoopReceiptCleanupStatus.AuditUnavailable => "Cleanup could not prove required audit durability; no additional cleanup was attempted.",
            CustomLoopReceiptCleanupStatus.CleanupConflict => "Receipt evidence changed during cleanup; the ambiguous evidence was preserved for review.",
            CustomLoopReceiptCleanupStatus.Corrupt => "Receipt evidence could not be validated safely; cleanup remains unavailable until repaired.",
            CustomLoopReceiptCleanupStatus.Degraded => "Receipt cleanup evidence is ambiguous and requires operator review.",
            CustomLoopReceiptCleanupStatus.Invalid => "The cleanup request or durable cleanup journal is invalid.",
            CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning => "Receipt evidence was compacted, but the terminal audit outcome requires review.",
            _ => "Receipt cleanup returned an unknown status; no further cleanup was attempted."
        };
    }

    private static LoopReceiptRetentionHealth MapJournalHealth(CustomLoopReceiptActiveCleanupJournalPosture journal)
    {
        if (journal.Stage is null) return LoopReceiptRetentionHealth.Healthy;
        if (journal.Outcome == CustomLoopReceiptCleanupOutcome.Corrupt) return LoopReceiptRetentionHealth.Corrupt;
        if (journal.Outcome == CustomLoopReceiptCleanupOutcome.AuditUnavailable) return LoopReceiptRetentionHealth.AuditUnavailable;
        if (journal.Stage is CustomLoopReceiptCleanupStage.Degraded or CustomLoopReceiptCleanupStage.AbandonedConflict) return LoopReceiptRetentionHealth.Degraded;
        return journal.Stage == CustomLoopReceiptCleanupStage.Completed ? LoopReceiptRetentionHealth.Healthy : LoopReceiptRetentionHealth.RecoveryPending;
    }

    private static string BlockReasonForJournal(CustomLoopReceiptActiveCleanupJournalPosture journal)
    {
        if (journal.Stage is null) return CustomLoopReceiptCleanupBlockReason.None.ToString();
        if (journal.Outcome == CustomLoopReceiptCleanupOutcome.Corrupt) return CustomLoopReceiptCleanupBlockReason.CorruptEvidence.ToString();
        if (journal.Outcome == CustomLoopReceiptCleanupOutcome.AuditUnavailable) return CustomLoopReceiptCleanupBlockReason.AuditUnavailable.ToString();
        if (journal.Stage == CustomLoopReceiptCleanupStage.Completed) return CustomLoopReceiptCleanupBlockReason.None.ToString();
        if (journal.Stage == CustomLoopReceiptCleanupStage.AbandonedConflict) return CustomLoopReceiptCleanupBlockReason.CleanupConflict.ToString();
        if (journal.Stage == CustomLoopReceiptCleanupStage.Degraded) return CustomLoopReceiptCleanupBlockReason.DegradedEvidence.ToString();
        return CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved.ToString();
    }

    private static LoopReceiptRetentionHealth MostSevere(LoopReceiptRetentionHealth left, LoopReceiptRetentionHealth right)
    {
        return GetSeverity(left) >= GetSeverity(right) ? left : right;
    }

    private static int GetSeverity(LoopReceiptRetentionHealth health)
    {
        return health switch
        {
            LoopReceiptRetentionHealth.Corrupt => 7,
            LoopReceiptRetentionHealth.AuditUnavailable => 6,
            LoopReceiptRetentionHealth.OwnershipConflict => 5,
            LoopReceiptRetentionHealth.RecoveryPending => 4,
            LoopReceiptRetentionHealth.Degraded => 3,
            LoopReceiptRetentionHealth.Exhausted => 2,
            _ => 1
        };
    }
}
