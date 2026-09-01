using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopCoordinatorRepairFacadeTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Theory]
    [InlineData(GovernedLoopCoordinatorRepairSubmitStatus.Invalid, GovernedLoopCoordinatorRepairExecutionStatus.Invalid)]
    [InlineData(GovernedLoopCoordinatorRepairSubmitStatus.Unauthorized, GovernedLoopCoordinatorRepairExecutionStatus.Unauthorized)]
    [InlineData(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, GovernedLoopCoordinatorRepairExecutionStatus.Conflict)]
    [InlineData(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, GovernedLoopCoordinatorRepairExecutionStatus.Corrupt)]
    [InlineData(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, GovernedLoopCoordinatorRepairExecutionStatus.Unavailable)]
    public async Task SubmitAsync_projects_nonaccepted_service_outcomes_without_starting_work(
        GovernedLoopCoordinatorRepairSubmitStatus submissionStatus,
        GovernedLoopCoordinatorRepairExecutionStatus expectedStatus)
    {
        var service = new StubRepairService { SubmitResult = Submission(submissionStatus) };
        var host = new RecordingStartupPort();
        var facade = new GovernedLoopCoordinatorRepairFacade(service, host);

        var result = await facade.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(RepairDisposition()));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Start);
        Assert.Equal(0, host.StartCount);
    }

    [Theory]
    [InlineData(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, GovernedLoopCoordinatorRepairExecutionStatus.Repaired)]
    [InlineData(AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning, GovernedLoopCoordinatorRepairExecutionStatus.Repaired)]
    [InlineData(AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer, GovernedLoopCoordinatorRepairExecutionStatus.Conflict)]
    [InlineData(AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable, GovernedLoopCoordinatorRepairExecutionStatus.Unavailable)]
    [InlineData(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, GovernedLoopCoordinatorRepairExecutionStatus.Conflict)]
    public async Task SubmitAsync_projects_canonical_start_outcome_after_accepted_repair(
        AgentRuntimeGovernedLoopBackgroundStartStatus startStatus,
        GovernedLoopCoordinatorRepairExecutionStatus expectedStatus)
    {
        var service = new StubRepairService { SubmitResult = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Accepted) };
        var host = new RecordingStartupPort { Result = Start(startStatus) };
        var facade = new GovernedLoopCoordinatorRepairFacade(service, host);

        var result = await facade.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(RepairDisposition()));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(startStatus, result.Start!.Status);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task SubmitAsync_replays_exact_start_and_fails_closed_when_the_host_is_unavailable()
    {
        var replayService = new StubRepairService { SubmitResult = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Replayed) };
        var replayHost = new RecordingStartupPort { Result = Start(AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning) };
        var replayFacade = new GovernedLoopCoordinatorRepairFacade(replayService, replayHost);
        var unavailableService = new StubRepairService { SubmitResult = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Accepted) };
        var unavailableHost = new RecordingStartupPort { Exception = new IOException("host unavailable") };
        var unavailableFacade = new GovernedLoopCoordinatorRepairFacade(unavailableService, unavailableHost);

        var replayed = await replayFacade.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(RepairDisposition()));
        var unavailable = await unavailableFacade.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(RepairDisposition()));

        Assert.Equal(GovernedLoopCoordinatorRepairExecutionStatus.Replayed, replayed.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairExecutionStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Start);
    }

    [Fact]
    public async Task SubmitAsync_preserves_caller_cancellation_from_the_canonical_start_host()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new StubRepairService { SubmitResult = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Accepted) };
        var host = new RecordingStartupPort { Exception = new OperationCanceledException(cancellation.Token) };
        var facade = new GovernedLoopCoordinatorRepairFacade(service, host);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.SubmitAsync(
            new GovernedLoopCoordinatorRepairSubmitRequest(RepairDisposition()),
            cancellation.Token));

        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task Preview_and_submit_reject_a_coordinator_not_owned_by_the_canonical_host_before_admission()
    {
        var service = new StubRepairService();
        var host = new RecordingStartupPort { CoordinatorId = "local-background" };
        var facade = new GovernedLoopCoordinatorRepairFacade(service, host);
        var foreign = RepairDisposition("foreign-background");

        var preview = await facade.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest(foreign.CoordinatorId, foreign.OperationId));
        var submitted = await facade.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(foreign));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Conflict, preview.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairExecutionStatus.Conflict, submitted.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, submitted.Submission.Status);
        Assert.Equal(0, service.PreviewCalls);
        Assert.Equal(0, service.SubmitCalls);
        Assert.Equal(0, host.StartCount);
    }

    private static GovernedLoopCoordinatorRepairSubmitResult Submission(GovernedLoopCoordinatorRepairSubmitStatus status)
        => new(status, "operation", null, "test");

    private static AgentRuntimeGovernedLoopBackgroundStartResult Start(AgentRuntimeGovernedLoopBackgroundStartStatus status)
        => new(status, AgentRuntimeGovernedLoopBackgroundReadiness.Ready, AgentRuntimeGovernedLoopBackgroundOwnership.Local, false, "test");

    private static GovernedLoopCoordinatorRepairDisposition RepairDisposition(string coordinatorId = "coordinator")
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            1,
            coordinatorId,
            "owner",
            1,
            _now,
            string.Empty));
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(
            1,
            _workspaceId,
            coordinatorId,
            true,
            true,
            true,
            true,
            true,
            _now,
            string.Empty));
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            1,
            _workspaceId,
            coordinatorId,
            "operation",
            "actor",
            ownership,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            readiness,
            _now,
            string.Empty));
    }

    private sealed class RecordingStartupPort : IGovernedLoopCoordinatorRepairStartupPort
    {
        public string CoordinatorId { get; init; } = "coordinator";

        internal Exception? Exception { get; init; }

        internal AgentRuntimeGovernedLoopBackgroundStartResult Result { get; init; } = Start(AgentRuntimeGovernedLoopBackgroundStartStatus.Started);

        internal int StartCount { get; private set; }

        public Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartAfterRepairAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class StubRepairService : IGovernedLoopCoordinatorRepairService
    {
        internal int PreviewCalls { get; private set; }

        internal int SubmitCalls { get; private set; }

        internal GovernedLoopCoordinatorRepairSubmitResult SubmitResult { get; init; } = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Invalid);

        public Task<GovernedLoopCoordinatorRepairPreview> PreviewAsync(
            GovernedLoopCoordinatorRepairPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            return Task.FromResult(new GovernedLoopCoordinatorRepairPreview(
                GovernedLoopCoordinatorRepairPreviewStatus.Ready,
                request.OperationId,
                RepairDisposition(request.CoordinatorId),
                "test"));
        }

        public Task<GovernedLoopCoordinatorRepairSubmitResult> SubmitAsync(
            GovernedLoopCoordinatorRepairSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(SubmitResult);
        }
    }
}
