using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostCurrentPosturePort(
    ICustomLoopRunStore runs,
    TimeProvider timeProvider,
    int postureGateReadOrdinal = 0,
    string? postureGateReadyPath = null,
    string? postureGateReleasePath = null) : IGovernedLoopSleepCurrentPosturePort
{
    private const string AuthorityHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private readonly ICustomLoopRunStore _runs = runs;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly HumanInputResponseContinuationHostPostureGate? _gate = HumanInputResponseContinuationHostPostureGate.Create(
        postureGateReadOrdinal,
        postureGateReadyPath,
        postureGateReleasePath);
    private int _readCount;

    internal string? ExpectedPostureHash { get; private set; }

    internal int? ExpectedLifecycleVersion { get; private set; }

    internal string? ObservedPostureHash { get; private set; }

    internal int? ObservedLifecycleVersion { get; private set; }

    public async Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
        GovernedLoopExecutionBinding binding,
        CancellationToken cancellationToken = default)
    {
        var readOrdinal = Interlocked.Increment(ref _readCount);
        _gate?.WaitIfMatched(readOrdinal);
        var run = await _runs.GetAsync(binding.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.SequentialAdapterBinding is not { } adapter || run.Frontier is null || !Equals(adapter.ExecutionBinding, binding))
        {
            return new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.NotFound);
        }

        var lifecycle = GovernedLoopRunLifecycle.Create(
            binding,
            GovernedLoopRunLifecyclePayload.Create(
                1,
                run.LifecycleVersion,
                Map(run.Status),
                run.CreatedAtUtc,
                run.UpdatedAtUtc,
                run.IsTerminal ? run.UpdatedAtUtc : null));
        var posture = new GovernedLoopSleepCurrentPosture(
            GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, run.Frontier, [], []),
            adapter.AdmissionReceipt.Intent.Publication,
            true,
            AuthorityHash,
            null,
            _timeProvider.GetUtcNow(),
            PostureHash(run));
        if (readOrdinal == ((_gate?.ReadOrdinal ?? 3) - 1))
        {
            ExpectedPostureHash = posture.PostureHash;
            ExpectedLifecycleVersion = run.LifecycleVersion;
        }
        ObservedPostureHash = posture.PostureHash;
        ObservedLifecycleVersion = run.LifecycleVersion;
        return new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.Found, posture);
    }

    private static string PostureHash(CustomLoopRunRecord run)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            run.LifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + run.Status
            + ":" + run.Frontier?.Payload.ContentHash)));

    private static GovernedLoopRunStatus Map(CustomLoopRunStatus status)
        => status switch
        {
            CustomLoopRunStatus.Admitted => GovernedLoopRunStatus.Admitted,
            CustomLoopRunStatus.Running => GovernedLoopRunStatus.Running,
            CustomLoopRunStatus.Waiting => GovernedLoopRunStatus.Waiting,
            CustomLoopRunStatus.PauseRequested => GovernedLoopRunStatus.PauseRequested,
            CustomLoopRunStatus.Paused => GovernedLoopRunStatus.Paused,
            CustomLoopRunStatus.CancelRequested => GovernedLoopRunStatus.CancelRequested,
            CustomLoopRunStatus.Completed => GovernedLoopRunStatus.Completed,
            CustomLoopRunStatus.Failed => GovernedLoopRunStatus.Failed,
            CustomLoopRunStatus.Cancelled => GovernedLoopRunStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => GovernedLoopRunStatus.NeedsReview,
            _ => GovernedLoopRunStatus.Unknown,
        };
}
