using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Web.Services;

internal sealed class PendingApproval
{
    private readonly TaskCompletionSource<ToolApprovalResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingApproval(ToolApprovalRequest request, long sequence, DateTimeOffset createdAtUtc)
    {
        Request = request;
        Sequence = sequence;
        CreatedAtUtc = createdAtUtc;
    }

    public ToolApprovalRequest Request { get; }

    public long Sequence { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public Task<ToolApprovalResponse> WaitAsync(CancellationToken cancellationToken)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    public bool TrySetResult(ToolApprovalResponse response)
    {
        return _completion.TrySetResult(response);
    }

    public bool TryCancel(CancellationToken cancellationToken)
    {
        return _completion.TrySetCanceled(cancellationToken);
    }
}
