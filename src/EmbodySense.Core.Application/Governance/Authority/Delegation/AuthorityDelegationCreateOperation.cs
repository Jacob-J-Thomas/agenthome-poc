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

    internal bool Complete(bool publish)
    {
        lock (_waitersSync)
        {
            publish &= !_executionCancellation.IsCancellationRequested;
            _completed = true;
            if (_waiters == 0)
            {
                _executionCancellation.Dispose();
            }

            return publish;
        }
    }

    internal void ReleaseWaiter()
    {
        lock (_waitersSync)
        {
            _waiters--;
            if (_waiters != 0)
            {
                return;
            }

            if (_completed)
            {
                _executionCancellation.Dispose();
                return;
            }

            _executionCancellation.Cancel();
        }
    }
}
