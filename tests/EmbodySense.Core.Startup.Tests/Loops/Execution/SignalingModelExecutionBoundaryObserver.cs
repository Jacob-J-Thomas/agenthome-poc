using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal sealed class SignalingModelExecutionBoundaryObserver(
    GovernedModelPrimaryExecutionBoundary target) : IGovernedModelPrimaryExecutionBoundaryObserver
{
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask ObserveAsync(
        GovernedModelPrimaryExecutionBoundary boundary,
        CancellationToken _ = default)
    {
        if (boundary == target)
        {
            _observed.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }

    internal async Task WaitForObservationAsync(Task blockerTask)
    {
        Task completed;
        try
        {
            completed = await Task.WhenAny(_observed.Task, blockerTask).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            if (blockerTask.IsCompleted)
            {
                await blockerTask;
            }

            throw new Xunit.Sdk.XunitException($"The blocking trigger worker did not retain its pre-provider reservation within the test deadline. BlockerStatus={blockerTask.Status}.");
        }

        if (ReferenceEquals(completed, _observed.Task))
        {
            await _observed.Task;
            return;
        }

        await blockerTask;
        throw new Xunit.Sdk.XunitException("The blocking trigger worker completed before retaining its pre-provider reservation.");
    }
}
