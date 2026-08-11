using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopExecutionCancellationSignalGroupTests
{
    [Fact]
    public void TryRegisterActiveRun_rolls_back_the_first_registration_when_the_peer_rejects()
    {
        var primaryLease = new RecordingLease();
        var primary = new RecordingSignal { Registration = primaryLease };
        var secondary = new RecordingSignal { Registration = null };
        var group = new CustomLoopExecutionCancellationSignalGroup(primary, secondary);

        var registration = group.TryRegisterActiveRun("run-one");

        Assert.Null(registration);
        Assert.Equal(1, primary.RegistrationCalls);
        Assert.Equal(1, secondary.RegistrationCalls);
        Assert.Equal(1, primaryLease.DisposeCalls);
    }

    [Fact]
    public void TryRegisterActiveRun_disposes_both_registrations_exactly_once()
    {
        var primaryLease = new RecordingLease();
        var secondaryLease = new RecordingLease();
        var group = new CustomLoopExecutionCancellationSignalGroup(
            new RecordingSignal { Registration = primaryLease },
            new RecordingSignal { Registration = secondaryLease });

        var registration = Assert.IsAssignableFrom<IDisposable>(group.TryRegisterActiveRun("run-one"));
        registration.Dispose();
        registration.Dispose();

        Assert.Equal(1, primaryLease.DisposeCalls);
        Assert.Equal(1, secondaryLease.DisposeCalls);
    }

    [Fact]
    public void CancelActiveAttempt_reaches_the_active_peer_without_treating_the_inactive_runner_as_failure()
    {
        var inactive = new RecordingSignal { CancelException = new InvalidOperationException("not owned") };
        var active = new RecordingSignal();
        var group = new CustomLoopExecutionCancellationSignalGroup(inactive, active);

        group.CancelActiveAttempt("run-one");

        Assert.Equal(1, inactive.CancelCalls);
        Assert.Equal(1, active.CancelCalls);
    }

    [Fact]
    public async Task RequestActiveAttemptCancellationAsync_routes_through_the_shared_broker_once()
    {
        var primary = new RecordingSignal();
        var secondary = new RecordingSignal();
        var group = new CustomLoopExecutionCancellationSignalGroup(primary, secondary);

        var result = await group.RequestActiveAttemptCancellationAsync("run-one", "cancel-one");

        Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, result.Status);
        Assert.Equal(1, primary.RequestCalls);
        Assert.Equal(0, secondary.RequestCalls);
    }

    private sealed class RecordingSignal : ICustomLoopExecutionCancellationSignal
    {
        public IDisposable? Registration { get; init; } = new RecordingLease();

        public Exception? CancelException { get; init; }

        public int RegistrationCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int RequestCalls { get; private set; }

        public IDisposable? TryRegisterActiveRun(string runId)
        {
            RegistrationCalls++;
            return Registration;
        }

        public void CancelActiveAttempt(string runId)
        {
            CancelCalls++;
            if (CancelException is not null)
            {
                throw CancelException;
            }
        }

        public Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(
            string runId,
            string operationId,
            CancellationToken cancellationToken = default)
        {
            RequestCalls++;
            return Task.FromResult(new CustomLoopAttemptCancellationResult(
                CustomLoopAttemptCancellationStatus.SignalDelivered,
                "shared broker accepted the cancellation"));
        }
    }

    private sealed class RecordingLease : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose() => DisposeCalls++;
    }
}
