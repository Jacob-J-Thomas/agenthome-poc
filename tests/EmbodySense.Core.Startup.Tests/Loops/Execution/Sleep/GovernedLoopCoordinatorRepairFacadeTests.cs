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

    private static GovernedLoopCoordinatorRepairSubmitResult Submission(GovernedLoopCoordinatorRepairSubmitStatus status)
        => new(status, "operation", null, "test");

    private static AgentRuntimeGovernedLoopBackgroundStartResult Start(AgentRuntimeGovernedLoopBackgroundStartStatus status)
        => new(status, AgentRuntimeGovernedLoopBackgroundReadiness.Ready, AgentRuntimeGovernedLoopBackgroundOwnership.Local, false, "test");

    private static GovernedLoopCoordinatorRepairDisposition RepairDisposition()
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            1,
            "coordinator",
            "owner",
            1,
            _now,
            string.Empty));
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(
            1,
            _workspaceId,
            "coordinator",
            true,
            true,
            true,
            true,
            _now,
            string.Empty));
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            1,
            _workspaceId,
            "coordinator",
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
        internal Exception? Exception { get; init; }

        internal AgentRuntimeGovernedLoopBackgroundStartResult Result { get; init; } = Start(AgentRuntimeGovernedLoopBackgroundStartStatus.Started);

        internal int StartCount { get; private set; }

        public Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartAsync(CancellationToken cancellationToken = default)
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
        internal GovernedLoopCoordinatorRepairSubmitResult SubmitResult { get; init; } = Submission(GovernedLoopCoordinatorRepairSubmitStatus.Invalid);

        public Task<GovernedLoopCoordinatorRepairPreview> PreviewAsync(
            GovernedLoopCoordinatorRepairPreviewRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedLoopCoordinatorRepairSubmitResult> SubmitAsync(
            GovernedLoopCoordinatorRepairSubmitRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SubmitResult);
    }
}
