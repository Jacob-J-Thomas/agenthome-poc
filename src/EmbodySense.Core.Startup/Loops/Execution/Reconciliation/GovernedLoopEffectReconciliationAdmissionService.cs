using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

/// <summary>Publishes exact durable actuator ambiguities into the shared non-notifying reconciliation attention store.</summary>
internal sealed class GovernedLoopEffectReconciliationAdmissionService : IGovernedLoopEffectReconciliationAdmissionService
{
    private const int MaximumRecoveryPages = (CustomLoopLimits.MaxRunTracesPerWorkspace + CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace + CustomLoopLimits.MaxRecentRunsPageSize - 1) / CustomLoopLimits.MaxRecentRunsPageSize;
    private readonly IGovernedLoopEffectAttemptReadStore _effects;
    private readonly GovernedLoopEffectReconciliationProbeRegistry _registry;
    private readonly ICustomLoopRunStore _runs;
    private readonly IGovernedLoopEffectReconciliationService _service;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    internal GovernedLoopEffectReconciliationAdmissionService(
        string workspaceId,
        ICustomLoopRunStore runs,
        IGovernedLoopEffectAttemptReadStore effects,
        GovernedLoopEffectReconciliationProbeRegistry registry,
        IGovernedLoopEffectReconciliationService service,
        TimeProvider timeProvider)
    {
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GovernedLoopEffectReconciliationAdmissionResult> AdmitAsync(CustomLoopRunRecord run, GovernedLoopEffectReconciliationBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CustomLoopRunValidator.Validate(run).IsValid)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Corrupt);
        }
        if (!TryReadCandidate(run, binding, out var ambiguity, out var frontier))
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Conflict);
        }

        GovernedLoopEffectAttemptReadResult effectRead;
        try
        {
            effectRead = await _effects.ReadAsync(binding.WorkspaceId, binding.OperationId, binding.EffectGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Unavailable);
        }

        if (effectRead.Status == GovernedLoopEffectAttemptReadStatus.Missing)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.RepairRequired);
        }
        if (effectRead.Status == GovernedLoopEffectAttemptReadStatus.Corrupt)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Corrupt);
        }
        if (effectRead.Status != GovernedLoopEffectAttemptReadStatus.Current || effectRead.Attempt is null)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Unavailable);
        }

        GovernedLoopEffectReconciliationBinding currentBinding;
        try
        {
            currentBinding = GovernedLoopEffectReconciliationContract.CreateBinding(_workspaceId, binding.ActivationOrdinal, binding.VisitOrdinal, effectRead.Attempt);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Corrupt);
        }
        if (!Equals(currentBinding, binding))
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Conflict);
        }
        if (!_registry.TryResolve(effectRead.Attempt, out var metadata) || metadata is null)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.RepairRequired);
        }

        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
        }
        catch
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Unavailable);
        }
        if (now == default || now.Offset != TimeSpan.Zero || now < run.UpdatedAtUtc)
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Unavailable);
        }

        var caseId = "case-effect-reconciliation-" + binding.ContentHash;
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            caseId,
            binding.ContentHash,
            "source-effect-reconciliation-" + metadata.ContentHash,
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            metadata.ContractId,
            metadata.ContractVersion,
            metadata.ContentHash,
            Hash("source-registration", binding.ContentHash, metadata.ContentHash, run.SequentialAdapterBinding!.AdmissionReceiptHash, frontier!.Payload.ContentHash, ambiguity!.SequentialNodeEvidence!.EvidenceHash),
            run.UpdatedAtUtc,
            null,
            string.Empty));
        var receipts = new[]
        {
            run.SequentialAdapterBinding.AdmissionReceiptHash,
            frontier.Payload.ContentHash,
            ambiguity.SequentialNodeEvidence.EvidenceHash,
        }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        GovernedLoopEffectReconciliationOperationResult opened;
        try
        {
            opened = await _service.OpenAsync(
                new GovernedLoopEffectReconciliationOpenRequest(
                    "open-effect-reconciliation-" + binding.ContentHash,
                    caseId,
                    binding,
                    metadata,
                    [source],
                    receipts),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopEffectReconciliationAdmissionStatus.Unavailable);
        }

        return Result(opened.Status switch
        {
            GovernedLoopEffectReconciliationOperationStatus.Applied => GovernedLoopEffectReconciliationAdmissionStatus.Opened,
            GovernedLoopEffectReconciliationOperationStatus.Replayed => GovernedLoopEffectReconciliationAdmissionStatus.Replayed,
            GovernedLoopEffectReconciliationOperationStatus.NotFound => GovernedLoopEffectReconciliationAdmissionStatus.RepairRequired,
            GovernedLoopEffectReconciliationOperationStatus.Denied => GovernedLoopEffectReconciliationAdmissionStatus.Denied,
            GovernedLoopEffectReconciliationOperationStatus.Conflict => GovernedLoopEffectReconciliationAdmissionStatus.Conflict,
            GovernedLoopEffectReconciliationOperationStatus.Invalid => GovernedLoopEffectReconciliationAdmissionStatus.Invalid,
            GovernedLoopEffectReconciliationOperationStatus.Corrupt => GovernedLoopEffectReconciliationAdmissionStatus.Corrupt,
            GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded => GovernedLoopEffectReconciliationAdmissionStatus.CapacityExceeded,
            GovernedLoopEffectReconciliationOperationStatus.RepairRequired => GovernedLoopEffectReconciliationAdmissionStatus.RepairRequired,
            _ => GovernedLoopEffectReconciliationAdmissionStatus.Unavailable,
        });
    }

    internal async Task<GovernedLoopEffectReconciliationAdmissionStatus> RecoverAsync(CancellationToken cancellationToken = default)
    {
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var aggregate = GovernedLoopEffectReconciliationAdmissionStatus.NotApplicable;
        for (var pageIndex = 0; pageIndex < MaximumRecoveryPages; pageIndex++)
        {
            CustomLoopRunPage page;
            try
            {
                page = await _runs.ListPageAsync(new CustomLoopRunPageRequest(CustomLoopLimits.MaxRecentRunsPageSize, Cursor: cursor), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FormatException)
            {
                return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
            }
            catch
            {
                return GovernedLoopEffectReconciliationAdmissionStatus.Unavailable;
            }
            if (page?.Items is null || page.Items.Count > CustomLoopLimits.MaxRecentRunsPageSize)
            {
                return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
            }

            foreach (var summary in page.Items)
            {
                if (summary is null || !runIds.Add(summary.Id))
                {
                    return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
                }
                if (summary.Status != CustomLoopRunStatus.NeedsReview || summary.IsDeleted)
                {
                    continue;
                }

                CustomLoopRunRecord? run;
                try
                {
                    run = await _runs.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FormatException)
                {
                    return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
                }
                catch
                {
                    return GovernedLoopEffectReconciliationAdmissionStatus.Unavailable;
                }
                var bindings = run?.Events.Where(item => item?.EffectReconciliationBinding is not null).Select(item => item.EffectReconciliationBinding!).Take(2).ToArray() ?? [];
                if (bindings.Length > 1)
                {
                    return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
                }
                if (run is null || bindings.Length == 0)
                {
                    continue;
                }

                var admitted = await AdmitAsync(run, bindings[0], cancellationToken).ConfigureAwait(false);
                if (admitted.Status is not (GovernedLoopEffectReconciliationAdmissionStatus.Opened or GovernedLoopEffectReconciliationAdmissionStatus.Replayed))
                {
                    return admitted.Status;
                }
                if (admitted.Status is GovernedLoopEffectReconciliationAdmissionStatus.Opened or GovernedLoopEffectReconciliationAdmissionStatus.Replayed)
                {
                    aggregate = admitted.Status;
                }
            }

            if (page.ContinuationCursor is null)
            {
                return aggregate;
            }
            if (!cursors.Add(page.ContinuationCursor))
            {
                return GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
            }
            cursor = page.ContinuationCursor;
        }

        return cursor is null ? aggregate : GovernedLoopEffectReconciliationAdmissionStatus.Corrupt;
    }

    private bool TryReadCandidate(CustomLoopRunRecord run, GovernedLoopEffectReconciliationBinding binding, out CustomLoopRunEvent? ambiguity, out GovernedLoopFrontierPosture? frontier)
    {
        ambiguity = null;
        frontier = run.Frontier;
        if (run.Status != CustomLoopRunStatus.NeedsReview
            || run.SequentialAdapterBinding is null
            || frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
            || !string.Equals(binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return false;
        }

        var matches = run.Events.Where(item => item?.EffectReconciliationBinding is not null && Equals(item.EffectReconciliationBinding, binding)).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].SequentialNodeEvidence is not { } evidence)
        {
            return false;
        }
        var nodes = frontier.Payload.Nodes.Where(node => node.ActivationOrdinal == binding.ActivationOrdinal
            && node.VisitOrdinal == binding.VisitOrdinal
            && string.Equals(node.NodeId, binding.NodeId, StringComparison.Ordinal)
            && node.Attempt == binding.NodeAttempt
            && node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked
            && string.Equals(node.OutcomeEvidenceId, matches[0].EventId, StringComparison.Ordinal)
            && string.Equals(node.OutcomeEvidenceHash, evidence.OutcomeArtifactHash, StringComparison.Ordinal)).Take(2).ToArray();
        if (nodes.Length != 1)
        {
            return false;
        }

        ambiguity = matches[0];
        return true;
    }

    private static GovernedLoopEffectReconciliationAdmissionResult Result(GovernedLoopEffectReconciliationAdmissionStatus status)
        => new(status);

    private static string Hash(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.reconciliation-attention-admission.v1\n").Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
