using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class ConversationPublicationCommitProtocolTests
{
    [Fact]
    public async Task Exact_callback_runs_once_while_the_caller_boundary_is_active()
    {
        var boundaryActive = false;
        var appendCount = 0;

        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            async (commitAppend, cancellationToken) =>
            {
                boundaryActive = true;
                try
                {
                    await commitAppend(cancellationToken);
                }
                finally
                {
                    boundaryActive = false;
                }
            },
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.True(boundaryActive);
                appendCount++;
                return Task.FromResult("committed");
            });

        Assert.Equal(ConversationPublicationCommitProtocolStatus.Completed, result.Status);
        Assert.Equal("committed", result.Value);
        Assert.Null(result.Failure);
        Assert.Equal(1, result.CallbackInvocationCount);
        Assert.Equal(1, appendCount);
        Assert.False(boundaryActive);
    }

    [Theory]
    [InlineData(false, ConversationPublicationCommitProtocolStatus.CallbackNotInvoked)]
    [InlineData(true, ConversationPublicationCommitProtocolStatus.BoundaryFailed)]
    public async Task Boundary_that_never_crosses_the_callback_cannot_append(bool throwFromBoundary, ConversationPublicationCommitProtocolStatus expectedStatus)
    {
        var appendCount = 0;

        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            (_, _) => throwFromBoundary ? Task.FromException(new IOException("boundary failed")) : Task.CompletedTask,
            _ =>
            {
                appendCount++;
                return Task.FromResult("unexpected");
            });

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(0, result.CallbackInvocationCount);
        Assert.Equal(0, appendCount);
        Assert.Equal(throwFromBoundary, result.Failure is IOException);
    }

    [Fact]
    public async Task Caught_second_callback_is_reported_without_a_second_append()
    {
        var appendCount = 0;

        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            async (commitAppend, cancellationToken) =>
            {
                await commitAppend(cancellationToken);
                _ = await Assert.ThrowsAsync<InvalidOperationException>(() => commitAppend(cancellationToken));
            },
            _ => Task.FromResult($"commit-{++appendCount}"));

        Assert.Equal(ConversationPublicationCommitProtocolStatus.CallbackInvokedMultipleTimes, result.Status);
        Assert.Equal("commit-1", result.Value);
        Assert.Equal(2, result.CallbackInvocationCount);
        Assert.Equal(1, appendCount);
    }

    [Fact]
    public async Task Returning_before_callback_completion_cancels_the_append_lifetime_and_fails_closed()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCancellation = new TaskCompletionSource<string>();
        var appendCount = 0;

        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            async (commitAppend, cancellationToken) =>
            {
                _ = commitAppend(cancellationToken);
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            },
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => callbackCancellation.TrySetException(new OperationCanceledException(cancellationToken)));
                callbackEntered.TrySetResult();
                await callbackCancellation.Task;
                appendCount++;
                return "unexpected";
            });

        Assert.Equal(ConversationPublicationCommitProtocolStatus.CallbackIncomplete, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(1, result.CallbackInvocationCount);
        Assert.Equal(0, appendCount);
    }

    [Fact]
    public async Task Callback_captured_until_after_boundary_return_cannot_append()
    {
        Func<CancellationToken, Task>? captured = null;
        var appendCount = 0;
        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            (commitAppend, _) =>
            {
                captured = commitAppend;
                return Task.CompletedTask;
            },
            _ =>
            {
                appendCount++;
                return Task.FromResult("unexpected");
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => captured!(CancellationToken.None));

        Assert.Contains("after its boundary returns", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConversationPublicationCommitProtocolStatus.CallbackNotInvoked, result.Status);
        Assert.Equal(0, appendCount);
    }

    [Fact]
    public async Task Boundary_failure_after_callback_preserves_only_the_exact_callback_result_as_uncertain_evidence()
    {
        var result = await ConversationPublicationCommitProtocol.ExecuteAsync(
            async (commitAppend, cancellationToken) =>
            {
                await commitAppend(cancellationToken);
                throw new IOException("boundary failed after append");
            },
            _ => Task.FromResult("exact-append-result"));

        Assert.Equal(ConversationPublicationCommitProtocolStatus.BoundaryFailed, result.Status);
        Assert.Equal("exact-append-result", result.Value);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(1, result.CallbackInvocationCount);
    }
}
