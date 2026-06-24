using System.Collections.Concurrent;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebApprovalCoordinator : IToolApprovalPrompt
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private long _lastSequence;

    public async Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pending = new PendingApproval(request, Interlocked.Increment(ref _lastSequence), DateTimeOffset.UtcNow);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            throw new InvalidOperationException($"Approval request `{request.RequestId}` is already pending.");
        }

        try
        {
            using var registration = cancellationToken.Register(() => pending.TryCancel(cancellationToken));
            return await pending.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public IReadOnlyList<WebPendingApproval> GetPending()
    {
        return _pending.Values
            .OrderBy(item => item.Sequence)
            .Select(item => WebPendingApproval.FromRequest(item.Request, item.Sequence, item.CreatedAtUtc))
            .ToArray();
    }

    public WebApprovalDecisionResult SubmitDecision(string requestId, bool approved, string? detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (!_pending.TryRemove(requestId, out var pending))
        {
            return WebApprovalDecisionResult.NotFound(requestId);
        }

        var responseDetail = string.IsNullOrWhiteSpace(detail)
            ? (approved ? "Approved in the localhost web client." : "Rejected in the localhost web client.")
            : detail.Trim();
        var response = approved
            ? ToolApprovalResponse.Approve("human.web", responseDetail)
            : ToolApprovalResponse.Reject("human.web", responseDetail);

        return pending.TrySetResult(response)
            ? WebApprovalDecisionResult.Completed(requestId)
            : WebApprovalDecisionResult.AlreadyCompleted(requestId);
    }

    private sealed class PendingApproval
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
}
