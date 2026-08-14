using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Keeps two ordered runners under one lifecycle ownership lease while routing cross-process
/// cancellation exactly once through their shared cancellation broker.
/// </summary>
public sealed class CustomLoopExecutionCancellationSignalGroup : ICustomLoopExecutionCancellationSignal
{
    private readonly ICustomLoopExecutionCancellationSignal _primary;
    private readonly ICustomLoopExecutionCancellationSignal _secondary;

    /// <summary>Creates a lifecycle signal over the retained legacy and canonical ordered runners.</summary>
    /// <param name="primary">The primary runner used for the single shared-broker cancellation request.</param>
    /// <param name="secondary">The peer runner registered under the same lifecycle ownership lease.</param>
    public CustomLoopExecutionCancellationSignalGroup(
        ICustomLoopExecutionCancellationSignal primary,
        ICustomLoopExecutionCancellationSignal secondary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        if (ReferenceEquals(primary, secondary))
        {
            throw new ArgumentException("Lifecycle cancellation signals must be distinct runner instances.", nameof(secondary));
        }
    }

    /// <inheritdoc />
    public IDisposable? TryRegisterActiveRun(string runId)
    {
        var primary = _primary.TryRegisterActiveRun(runId);
        if (primary is null)
        {
            return null;
        }

        try
        {
            var secondary = _secondary.TryRegisterActiveRun(runId);
            if (secondary is null)
            {
                primary.Dispose();
                return null;
            }

            return new CustomLoopExecutionCancellationSignalGroupRegistration(primary, secondary);
        }
        catch
        {
            primary.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void CancelActiveAttempt(string runId)
    {
        InvalidOperationException? inactivePrimary = null;
        var delivered = false;
        try
        {
            _primary.CancelActiveAttempt(runId);
            delivered = true;
        }
        catch (InvalidOperationException exception)
        {
            inactivePrimary = exception;
        }

        try
        {
            _secondary.CancelActiveAttempt(runId);
            delivered = true;
        }
        catch (InvalidOperationException)
        {
            // The peer does not own this attempt; either the selected runner accepted the signal,
            // or the retained primary ownership failure is reported below.
        }

        if (!delivered)
        {
            throw inactivePrimary ?? new InvalidOperationException("Neither ordered runner owned the active provider attempt.");
        }
    }

    /// <inheritdoc />
    public Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(
        string runId,
        string operationId,
        CancellationToken cancellationToken = default)
        => _primary.RequestActiveAttemptCancellationAsync(runId, operationId, cancellationToken);

}
