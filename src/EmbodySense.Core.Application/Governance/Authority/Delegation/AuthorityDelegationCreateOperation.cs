using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Retains one reserved envelope creation until it either commits or conclusively fails.</summary>
internal sealed class AuthorityDelegationCreateOperation
{
    private readonly object _waitersSync = new();
    private readonly CancellationTokenSource _executionCancellation = new();
    private int _waiters = 1;
    private bool _completed;

    internal AuthorityDelegationCreateOperation(AuthorityDelegationCreateRequest request)
    {
        Request = request;
        Completion = new TaskCompletionSource<AuthorityDelegationServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal AuthorityDelegationCreateRequest Request { get; }

    internal TaskCompletionSource<AuthorityDelegationServiceResult> Completion { get; }

    internal CancellationToken ExecutionCancellationToken => _executionCancellation.Token;

    internal void AddWaiter()
    {
        lock (_waitersSync)
        {
            _waiters++;
        }
    }

    internal void Complete()
    {
        lock (_waitersSync)
        {
            _completed = true;
        }

        _executionCancellation.Dispose();
    }

    internal void ReleaseWaiter()
    {
        var cancelExecution = false;
        lock (_waitersSync)
        {
            _waiters--;
            cancelExecution = _waiters == 0 && !_completed;
        }

        if (cancelExecution)
        {
            _executionCancellation.Cancel();
        }
    }
}
