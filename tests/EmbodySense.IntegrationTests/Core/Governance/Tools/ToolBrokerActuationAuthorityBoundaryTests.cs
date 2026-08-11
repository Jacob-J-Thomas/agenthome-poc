using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.Core.Governance.Tools;

public sealed class ToolBrokerActuationAuthorityBoundaryTests
{
    [Fact]
    public async Task Direct_boundary_owns_the_exact_actuator_and_retention_follows_boundary_release()
    {
        using var workspace = await CreateWorkspaceAsync();
        var approval = new RecordingApprovalPrompt();
        var boundary = new AdversarialBoundary(BoundaryBehavior.Direct, () => approval.RequestCount == 1);
        var executor = new CountingWorkspaceToolExecutor(() => boundary.IsHeld);
        var retention = new RecordingRetentionStore(() => boundary.IsHeld);
        var broker = CreateBroker(workspace, approval, executor, retention, boundary);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, ".agent/skills/generated.md", "content"));

        Assert.True(result.Succeeded);
        Assert.Equal("actuator-output", result.OutputText);
        Assert.Equal(1, approval.RequestCount);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(1, retention.CallCount);
        Assert.False(boundary.IsHeld);
    }

    [Fact]
    public async Task Direct_disposition_without_callback_cannot_fabricate_a_tool_result()
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor();
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, new RecordingRetentionStore(), new AdversarialBoundary(BoundaryBehavior.DirectWithoutCallback));

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt")));

        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Caught_duplicate_callback_still_fails_after_only_one_actuator_call()
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor();
        var retention = new RecordingRetentionStore();
        var boundary = new AdversarialBoundary(BoundaryBehavior.CatchDuplicate);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, retention, boundary);

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => broker.ExecuteAsync(new ToolRequest(ToolCommand.Append, "shared/note.txt", "content")));

        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, retention.CallCount);
        Assert.True(boundary.DuplicateFailureCaught);
        var events = await new AuditLog(new WorkspacePaths(workspace.RootPath)).ReadTailAsync(20);
        Assert.Single(events, auditEvent => auditEvent.Action == "tool.execution.intent");
    }

    [Fact]
    public async Task Boundary_return_before_callback_completion_cancels_before_actuation()
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor(blockUntilCancelled: true, throwFromCancellationCallback: true);
        var boundary = new AdversarialBoundary(BoundaryBehavior.ReturnBeforeCompletion);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, new RecordingRetentionStore(), boundary);

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.IncompleteCallback!);

        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Callback_captured_then_invoked_after_boundary_return_cannot_actuate()
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor();
        var boundary = new AdversarialBoundary(BoundaryBehavior.CaptureLateCallback);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, new RecordingRetentionStore(), boundary);

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt")));
        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => boundary.InvokeLateAsync());

        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Post_approval_revocation_returns_definitive_denial_without_actuation()
    {
        using var workspace = await CreateWorkspaceAsync();
        var approval = new RecordingApprovalPrompt();
        var executor = new CountingWorkspaceToolExecutor();
        var boundary = new AdversarialBoundary(BoundaryBehavior.Denied, () => approval.RequestCount == 1);
        var broker = CreateBroker(workspace, approval, executor, new RecordingRetentionStore(), boundary);

        var result = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, ".agent/skills/generated.md", "content"));

        Assert.Equal(ToolExecutionOutcome.Denied, result.Outcome);
        Assert.Equal(1, approval.RequestCount);
        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData(ToolActuationAuthorityDisposition.ReviewRequired)]
    [InlineData(ToolActuationAuthorityDisposition.Ambiguous)]
    public async Task Review_or_ambiguous_authority_raises_non_denial_checkpoint_without_actuation(ToolActuationAuthorityDisposition disposition)
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor();
        var retention = new RecordingRetentionStore();
        var boundary = new AdversarialBoundary(disposition == ToolActuationAuthorityDisposition.ReviewRequired ? BoundaryBehavior.ReviewRequired : BoundaryBehavior.Ambiguous);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, retention, boundary);

        var exception = await Assert.ThrowsAsync<ToolActuationReviewRequiredException>(() => broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt")));

        Assert.Equal(disposition, exception.Disposition);
        Assert.IsNotType<InvalidOperationException>(exception);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, retention.CallCount);
        var events = await new AuditLog(new WorkspacePaths(workspace.RootPath)).ReadTailAsync(20);
        Assert.Contains(events, auditEvent => auditEvent.Action == "tool.loop_authority.evaluate" && auditEvent.Outcome == "needs_review");
    }

    [Theory]
    [InlineData(ToolActuationAuthorityDisposition.ReviewRequired)]
    [InlineData(ToolActuationAuthorityDisposition.Ambiguous)]
    public async Task Secondary_audit_failure_cannot_replace_the_exact_review_checkpoint(ToolActuationAuthorityDisposition disposition)
    {
        using var workspace = await CreateWorkspaceAsync();
        var executor = new CountingWorkspaceToolExecutor();
        var retention = new RecordingRetentionStore();
        var boundary = new AdversarialBoundary(disposition == ToolActuationAuthorityDisposition.ReviewRequired ? BoundaryBehavior.ReviewRequired : BoundaryBehavior.Ambiguous);
        var paths = new WorkspacePaths(workspace.RootPath);
        var audit = new FailingActuationAuthorityAuditLog(new AuditLog(paths));
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), executor, retention, boundary, audit);

        var exception = await Assert.ThrowsAsync<ToolActuationReviewRequiredException>(
            () => broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, "shared/note.txt")));

        Assert.Equal(disposition, exception.Disposition);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, retention.CallCount);
        Assert.Equal(1, audit.RejectedActuationAudits);
    }

    private static async Task<TestWorkspace> CreateWorkspaceAsync()
    {
        var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        return workspace;
    }

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt approval, IWorkspaceToolExecutor executor, IToolResultRetentionStore retention, IToolActuationAuthorityBoundary boundary, IAuditLog? auditLog = null)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = new PermissionPolicyStore().Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), approval, executor, auditLog ?? new AuditLog(paths), LoopDefinition.CreateDefaultConversation(), retention, actuationAuthorityBoundary: boundary);
    }

    private enum BoundaryBehavior
    {
        Direct,
        DirectWithoutCallback,
        CatchDuplicate,
        ReturnBeforeCompletion,
        CaptureLateCallback,
        Denied,
        ReviewRequired,
        Ambiguous
    }

    private sealed class AdversarialBoundary(BoundaryBehavior behavior, Func<bool>? precondition = null) : IToolActuationAuthorityBoundary
    {
        private Func<CancellationToken, Task>? _lateCallback;

        public bool IsHeld { get; private set; }

        public bool DuplicateFailureCaught { get; private set; }

        public Task? IncompleteCallback { get; private set; }

        public async Task<ToolActuationAuthorityExecution> ExecuteAsync<TResult>(ToolRequest request, string resolvedTargetPath, Func<ToolActuationAuthorityExecution, CancellationToken, Task<TResult>> executeActuatorAsync, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(resolvedTargetPath);
            ArgumentNullException.ThrowIfNull(executeActuatorAsync);
            if (precondition is not null && !precondition())
            {
                throw new InvalidOperationException("The authority boundary ran before the approved request was recorded.");
            }

            var execution = Execution(behavior switch
            {
                BoundaryBehavior.Denied => ToolActuationAuthorityDisposition.Denied,
                BoundaryBehavior.ReviewRequired => ToolActuationAuthorityDisposition.ReviewRequired,
                BoundaryBehavior.Ambiguous => ToolActuationAuthorityDisposition.Ambiguous,
                _ => ToolActuationAuthorityDisposition.Direct
            });
            switch (behavior)
            {
                case BoundaryBehavior.Direct:
                    IsHeld = true;
                    try
                    {
                        _ = await executeActuatorAsync(execution, cancellationToken);
                    }
                    finally
                    {
                        IsHeld = false;
                    }
                    break;
                case BoundaryBehavior.CatchDuplicate:
                    _ = await executeActuatorAsync(execution, cancellationToken);
                    try
                    {
                        _ = await executeActuatorAsync(execution, cancellationToken);
                    }
                    catch (ToolActuationAuthorityProtocolException)
                    {
                        DuplicateFailureCaught = true;
                    }
                    break;
                case BoundaryBehavior.ReturnBeforeCompletion:
                    IncompleteCallback = executeActuatorAsync(execution, cancellationToken);
                    break;
                case BoundaryBehavior.CaptureLateCallback:
                    _lateCallback = async token => _ = await executeActuatorAsync(execution, token);
                    break;
                case BoundaryBehavior.DirectWithoutCallback:
                case BoundaryBehavior.Denied:
                case BoundaryBehavior.ReviewRequired:
                case BoundaryBehavior.Ambiguous:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unsupported adversarial boundary behavior.");
            }

            return execution;
        }

        public Task InvokeLateAsync()
        {
            return (_lateCallback ?? throw new InvalidOperationException("No callback was captured."))(CancellationToken.None);
        }

        private static ToolActuationAuthorityExecution Execution(ToolActuationAuthorityDisposition disposition)
        {
            return new ToolActuationAuthorityExecution(disposition, $"test-{disposition}", new Dictionary<string, object?> { ["test_authority"] = disposition.ToString() });
        }
    }

    private sealed class CountingWorkspaceToolExecutor(Func<bool>? boundaryHeld = null, bool blockUntilCancelled = false, bool throwFromCancellationCallback = false) : IWorkspaceToolExecutor
    {
        public int CallCount { get; private set; }

        public Task<LocalWorkspaceResult> ListAsync(string resolvedPath, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        public Task<LocalWorkspaceResult> ReadAsync(string resolvedPath, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        public Task<LocalWorkspaceResult> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        public Task<LocalWorkspaceResult> AppendAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        public Task<LocalWorkspaceResult> WriteAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        public Task<LocalWorkspaceResult> DeleteAsync(string resolvedPath, CancellationToken cancellationToken = default) => ExecuteAsync(cancellationToken);

        private async Task<LocalWorkspaceResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (boundaryHeld is not null && !boundaryHeld())
            {
                throw new InvalidOperationException("The workspace actuator ran outside the authority boundary.");
            }

            if (blockUntilCancelled)
            {
                using var registration = throwFromCancellationCallback
                    ? cancellationToken.Register(() => throw new InvalidOperationException("hostile cancellation callback"))
                    : default;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            CallCount++;
            return new LocalWorkspaceResult("actuator-output", new Dictionary<string, object?> { ["actuator"] = "test" });
        }
    }

    private sealed class RecordingRetentionStore(Func<bool>? boundaryHeld = null) : IToolResultRetentionStore
    {
        public int CallCount { get; private set; }

        public Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default)
        {
            if (boundaryHeld?.Invoke() == true)
            {
                throw new InvalidOperationException("Tool result retention ran while the authority boundary was still held.");
            }

            CallCount++;
            return Task.FromResult(new ToolResultRetentionReference(ToolResultRetentionStatus.Retained, "retained/test.json", new string('a', 64), result.OutputText.Length, result.OutputText.Length, 1, DateTimeOffset.UtcNow, 0, "retained in test"));
        }
    }

    private sealed class FailingActuationAuthorityAuditLog(IAuditLog inner) : IAuditLog
    {
        public int RejectedActuationAudits { get; private set; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (auditEvent.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate
                && auditEvent.Metadata.TryGetValue("authority_phase", out var phase)
                && string.Equals(phase as string, "actuation_boundary", StringComparison.Ordinal))
            {
                RejectedActuationAudits++;
                throw new IOException("actuation authority audit unavailable");
            }

            return inner.AppendAsync(auditEvent, cancellationToken);
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
            => inner.ReadTailAsync(limit, cancellationToken);
    }

    private sealed class RecordingApprovalPrompt : IToolApprovalPrompt
    {
        public int RequestCount { get; private set; }

        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(ToolApprovalResponse.Approve("test", "approved"));
        }
    }

    private sealed class ThrowingApprovalPrompt : IToolApprovalPrompt
    {
        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Approval should not be required.");
        }
    }
}
