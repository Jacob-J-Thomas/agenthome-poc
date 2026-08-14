using EmbodySense.Core.Common.LocalWorkspace.Models;
using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>Captures one workspace actuator result without exposing it to the authority boundary.</summary>
internal sealed class ToolActuationCallbackGuard : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<ToolActuationAuthorityExecution, CancellationToken, Task> _beforeActuation;
    private readonly Func<CancellationToken, Task<LocalWorkspaceResult>> _actuator;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private int _attemptCount;
    private bool _closed;
    private bool _completed;
    private LocalWorkspaceResult? _result;
    private Exception? _expectedFailure;
    private ToolActuationAuthorityExecution? _directExecution;

    public ToolActuationCallbackGuard(Func<ToolActuationAuthorityExecution, CancellationToken, Task> beforeActuation, Func<CancellationToken, Task<LocalWorkspaceResult>> actuator, CancellationToken cancellationToken)
    {
        _beforeActuation = beforeActuation ?? throw new ArgumentNullException(nameof(beforeActuation));
        _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public async Task<bool> ExecuteAsync(ToolActuationAuthorityExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (_sync)
        {
            _attemptCount++;
            if (_closed || _attemptCount != 1)
            {
                throw new ToolActuationAuthorityProtocolException("The authority boundary attempted to invoke the single-use tool actuator more than once or after returning.");
            }

            if (execution.Disposition != ToolActuationAuthorityDisposition.Direct)
            {
                throw new ToolActuationAuthorityProtocolException("Only a direct authority decision may invoke the tool actuator continuation.");
            }

            if (string.IsNullOrWhiteSpace(execution.Detail) || execution.AuditMetadata is null)
            {
                throw new ToolActuationAuthorityProtocolException("The direct authority decision must include bounded detail and audit metadata before actuation.");
            }

            _directExecution = execution;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        await Task.Yield();
        lock (_sync)
        {
            if (_closed)
            {
                throw new ToolActuationAuthorityProtocolException("The authority boundary returned before its tool actuator continuation completed.");
            }
        }

        LocalWorkspaceResult? result = null;
        Exception? expectedFailure = null;
        await _beforeActuation(execution, linkedCancellation.Token);
        try
        {
            result = await _actuator(linkedCancellation.Token);
            ArgumentNullException.ThrowIfNull(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            expectedFailure = exception;
        }

        lock (_sync)
        {
            if (_closed)
            {
                throw new ToolActuationAuthorityProtocolException("The authority boundary returned before its tool actuator continuation completed.");
            }

            _result = result;
            _expectedFailure = expectedFailure;
            _completed = true;
        }

        return true;
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }

        try
        {
            _lifetimeCancellation.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // A hostile or defective callback cannot replace the broker's authority disposition or
            // continuation-protocol failure while the guard is being closed.
        }
    }

    public LocalWorkspaceResult GetCommittedResult(ToolActuationAuthorityExecution execution)
    {
        lock (_sync)
        {
            ValidateDirect(execution);
            return _result ?? throw new ToolActuationAuthorityProtocolException("The completed tool actuator did not capture a workspace result.");
        }
    }

    public Exception? GetExpectedFailure(ToolActuationAuthorityExecution execution)
    {
        lock (_sync)
        {
            ValidateDirect(execution);
            return _expectedFailure;
        }
    }

    public void ValidateNoActuation()
    {
        lock (_sync)
        {
            if (_attemptCount != 0 || _completed || _result is not null || _expectedFailure is not null)
            {
                throw new ToolActuationAuthorityProtocolException("A denied or review-required authority boundary invoked the tool actuator continuation.");
            }
        }
    }

    public void Dispose()
    {
        _lifetimeCancellation.Dispose();
    }

    private void ValidateDirect(ToolActuationAuthorityExecution execution)
    {
        if (_attemptCount != 1 || !_completed)
        {
            throw new ToolActuationAuthorityProtocolException("Direct tool authority requires exactly one actuator invocation that completes before the boundary returns.");
        }

        if (!ReferenceEquals(_directExecution, execution))
        {
            throw new ToolActuationAuthorityProtocolException("The authority boundary returned a different direct decision than the one supplied to the committed actuator.");
        }

        if ((_result is null) == (_expectedFailure is null))
        {
            throw new ToolActuationAuthorityProtocolException("The tool actuator must capture exactly one success result or one expected workspace failure.");
        }
    }
}
