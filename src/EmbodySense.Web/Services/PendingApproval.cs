using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Web.Services;

internal sealed class PendingApproval
{
    private readonly TaskCompletionSource<(bool Approved, string DecisionBy, string Detail)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingApproval(AgentToolApprovalRequest request, long sequence, DateTimeOffset createdAtUtc)
    {
        Request = request;
        Sequence = sequence;
        CreatedAtUtc = createdAtUtc;
    }

    public AgentToolApprovalRequest Request { get; }

    public long Sequence { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public Task<(bool Approved, string DecisionBy, string Detail)> WaitAsync(CancellationToken cancellationToken)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    public bool TrySetResult((bool Approved, string DecisionBy, string Detail) response)
    {
        return _completion.TrySetResult(response);
    }

    public bool TryCancel(CancellationToken cancellationToken)
    {
        return _completion.TrySetCanceled(cancellationToken);
    }
}
