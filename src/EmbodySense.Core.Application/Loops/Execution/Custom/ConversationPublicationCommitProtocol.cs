using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Runs one exact async append continuation while a caller-owned publication boundary remains active.</summary>
public static class ConversationPublicationCommitProtocol
{
    /// <summary>Invokes <paramref name="commitAppend"/> exactly once through <paramref name="boundary"/> and preserves its actual result.</summary>
    /// <typeparam name="T">The publisher-owned append result type.</typeparam>
    /// <param name="boundary">The caller-owned durable boundary.</param>
    /// <param name="commitAppend">The exact append continuation.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A protocol result that never substitutes a boundary-provided value for the callback result.</returns>
    public static async Task<ConversationPublicationCommitProtocolResult<T>> ExecuteAsync<T>(
        ConversationPublicationCommitBoundary boundary,
        Func<CancellationToken, Task<T>> commitAppend,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(commitAppend);

        T? value = null;
        Exception? callbackFailure = null;
        Exception? boundaryFailure = null;
        var callbackInvocationCount = 0;
        var callbackCompleted = 0;
        var callbackIncompleteWhenBoundaryClosed = false;
        var callbackStateSync = new object();
        var boundaryActive = true;
        using var boundaryLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await boundary(
                async token =>
                {
                    // A yielded continuation prevents a callback captured by a returning boundary from
                    // crossing into the append after the boundary has already closed.
                    await Task.Yield();
                    lock (callbackStateSync)
                    {
                        if (!boundaryActive)
                        {
                            throw new InvalidOperationException("The conversation publication append callback cannot run after its boundary returns.");
                        }

                        callbackInvocationCount++;
                        if (callbackInvocationCount != 1)
                        {
                            throw new InvalidOperationException("The conversation publication append callback may be invoked at most once.");
                        }
                    }

                    using var appendLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, boundaryLifetime.Token);
                    try
                    {
                        value = await commitAppend(appendLifetime.Token);
                    }
                    catch (Exception exception)
                    {
                        callbackFailure = exception;
                        throw;
                    }
                    finally
                    {
                        Volatile.Write(ref callbackCompleted, 1);
                    }
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            boundaryFailure = exception;
        }
        finally
        {
            lock (callbackStateSync)
            {
                boundaryActive = false;
                callbackIncompleteWhenBoundaryClosed = callbackInvocationCount > 0
                    && Volatile.Read(ref callbackCompleted) == 0;
            }
            await boundaryLifetime.CancelAsync();
        }

        var observedInvocationCount = Volatile.Read(ref callbackInvocationCount);
        if (observedInvocationCount == 0)
        {
            return new ConversationPublicationCommitProtocolResult<T>(
                boundaryFailure is null ? ConversationPublicationCommitProtocolStatus.CallbackNotInvoked : ConversationPublicationCommitProtocolStatus.BoundaryFailed,
                null,
                boundaryFailure,
                observedInvocationCount);
        }

        if (observedInvocationCount != 1)
        {
            return new ConversationPublicationCommitProtocolResult<T>(ConversationPublicationCommitProtocolStatus.CallbackInvokedMultipleTimes, value, boundaryFailure, observedInvocationCount);
        }

        if (callbackIncompleteWhenBoundaryClosed || Volatile.Read(ref callbackCompleted) == 0)
        {
            return new ConversationPublicationCommitProtocolResult<T>(ConversationPublicationCommitProtocolStatus.CallbackIncomplete, value, boundaryFailure, observedInvocationCount);
        }

        if (callbackFailure is not null || value is null)
        {
            return new ConversationPublicationCommitProtocolResult<T>(ConversationPublicationCommitProtocolStatus.CallbackFailed, value, callbackFailure ?? boundaryFailure, observedInvocationCount);
        }

        if (boundaryFailure is not null)
        {
            return new ConversationPublicationCommitProtocolResult<T>(ConversationPublicationCommitProtocolStatus.BoundaryFailed, value, boundaryFailure, observedInvocationCount);
        }

        return new ConversationPublicationCommitProtocolResult<T>(ConversationPublicationCommitProtocolStatus.Completed, value, null, observedInvocationCount);
    }
}
