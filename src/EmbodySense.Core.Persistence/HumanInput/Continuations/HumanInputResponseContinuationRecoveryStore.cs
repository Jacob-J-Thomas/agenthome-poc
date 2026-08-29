using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Persistence.HumanInput.Continuations.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Continuations;

/// <summary>Pages canonical run records to discover Human Input response continuations without creating a queue, lease, or response ledger.</summary>
/// <remarks>
/// Each invocation reads at most one canonical run and inspects at most the requested number of checkpoint ordinals.
/// The opaque cursor retains both the exclusive run cursor and an append-only checkpoint ordinal. A completed run is
/// tail-probed before its exclusive run cursor advances, so a checkpoint appended during discovery is not permanently
/// skipped. Only an empty run-store tail probe returns a null cursor and starts a fresh scan.
/// </remarks>
public sealed class HumanInputResponseContinuationRecoveryStore : IHumanInputResponseContinuationCandidateSource
{
    private readonly ICustomLoopRunStore _runs;

    /// <summary>Creates discovery over the sole canonical custom-loop run store.</summary>
    public HumanInputResponseContinuationRecoveryStore(ICustomLoopRunStore runs)
        => _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    /// <inheritdoc />
    public async Task<HumanInputResponseContinuationRecoveryPage> ListCandidatesAsync(
        int maximumCount,
        string? scanCursor,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > CustomLoopLimits.MaxRecentRunsPageSize
            || observedAtUtc == default
            || observedAtUtc.Offset != TimeSpan.Zero)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }

        HumanInputResponseContinuationRecoveryCursor cursor;
        try
        {
            cursor = HumanInputResponseContinuationRecoveryCursorCodec.Decode(scanCursor);
        }
        catch (ArgumentException)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }

        if (cursor.ResumeRunId is not null)
        {
            return await ResumeRunAsync(cursor, maximumCount, cancellationToken).ConfigureAwait(false);
        }

        CustomLoopRunPage source;
        try
        {
            source = await _runs.ListPageAsync(new CustomLoopRunPageRequest(1, null, cursor.AfterRunCursor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }
        catch (FormatException)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }
        catch
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Unavailable);
        }

        if (source?.Items is null || source.Items.Count > 1)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }
        if (source.ContinuationCursor is not null)
        {
            try
            {
                _ = CustomLoopRunPageCursorCodec.Decode(source.ContinuationCursor, null);
            }
            catch (ArgumentException)
            {
                return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
            }
        }
        if (source.Items.Count == 0)
        {
            return new HumanInputResponseContinuationRecoveryPage(HumanInputResponseContinuationRecoveryPageStatus.Current, [], null, false);
        }

        var summary = source.Items[0];
        if (summary is null || !CustomLoopArtifactIdentifier.IsValid(summary.Id))
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }

        var resume = new HumanInputResponseContinuationRecoveryCursor(
            1,
            cursor.AfterRunCursor,
            summary.Id,
            summary.CreatedAtUtc.UtcTicks,
            0);
        return await ResumeRunAsync(resume, maximumCount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanInputResponseContinuationRecoveryPage> ResumeRunAsync(
        HumanInputResponseContinuationRecoveryCursor cursor,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        try
        {
            run = await _runs.GetAsync(cursor.ResumeRunId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }
        catch
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Unavailable);
        }

        if (run is null)
        {
            return AdvanceAfterRun(cursor);
        }
        if (run.CreatedAtUtc.UtcTicks != cursor.ResumeRunCreatedAtUtcTicks
            || !CustomLoopRunValidator.Validate(run).IsValid)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }

        var checkpoints = run.HumanInputWaitingCheckpoints;
        if (cursor.NextCheckpointOrdinal > checkpoints.Count)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }
        if (cursor.NextCheckpointOrdinal == checkpoints.Count)
        {
            return AdvanceAfterRun(cursor);
        }

        var examinedThrough = Math.Min(checkpoints.Count, checked(cursor.NextCheckpointOrdinal + maximumCount));
        var candidates = new List<HumanInputResponseContinuationCandidate>(maximumCount);
        for (var index = cursor.NextCheckpointOrdinal; index < examinedThrough; index++)
        {
            var checkpoint = checkpoints[index];
            if (IsCandidate(run, checkpoint))
            {
                candidates.Add(new HumanInputResponseContinuationCandidate(run.Id, checkpoint.Binding.CheckpointId));
            }
        }
        if (candidates.Count > maximumCount
            || candidates.Select(item => string.Join('\n', item.RunId, item.CheckpointId)).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            return Page(HumanInputResponseContinuationRecoveryPageStatus.Invalid);
        }

        var next = new HumanInputResponseContinuationRecoveryCursor(
            1,
            cursor.AfterRunCursor,
            run.Id,
            run.CreatedAtUtc.UtcTicks,
            examinedThrough);
        return new HumanInputResponseContinuationRecoveryPage(
            HumanInputResponseContinuationRecoveryPageStatus.Current,
            candidates,
            HumanInputResponseContinuationRecoveryCursorCodec.Encode(next),
            true);
    }

    private static HumanInputResponseContinuationRecoveryPage AdvanceAfterRun(HumanInputResponseContinuationRecoveryCursor cursor)
    {
        var after = CustomLoopRunPageCursorCodec.Encode(new CustomLoopRunPageCursor(
            new DateTimeOffset(cursor.ResumeRunCreatedAtUtcTicks!.Value, TimeSpan.Zero),
            cursor.ResumeRunId!,
            null));
        var next = new HumanInputResponseContinuationRecoveryCursor(1, after, null, null, 0);
        return new HumanInputResponseContinuationRecoveryPage(
            HumanInputResponseContinuationRecoveryPageStatus.Current,
            [],
            HumanInputResponseContinuationRecoveryCursorCodec.Encode(next),
            true);
    }

    private static bool IsCandidate(CustomLoopRunRecord run, GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        return !run.IsTerminal
            && ((run.Status == CustomLoopRunStatus.Waiting
                && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Waiting
                && checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Pending or GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
                && activation is { Status: GovernedLoopNodeExecutionStatus.Waiting, Descriptor.Kind: GovernedLoopNodeKind.HumanInput })
                || (run.Status == CustomLoopRunStatus.Running
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Active
                    && checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Expired or GovernedLoopHumanInputWaitingCheckpointPosture.Rejected
                    && checkpoint.Evidence.LastOrDefault()?.Kind is GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired or GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected
                    && activation is { Status: GovernedLoopNodeExecutionStatus.Failed, Descriptor.Kind: GovernedLoopNodeKind.HumanInput, OutcomeEvidenceId: not null, OutcomeEvidenceHash: not null }));
    }

    private static HumanInputResponseContinuationRecoveryPage Page(HumanInputResponseContinuationRecoveryPageStatus status)
        => new(status, [], null, false);
}
