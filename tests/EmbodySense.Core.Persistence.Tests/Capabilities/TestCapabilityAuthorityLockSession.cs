namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class TestCapabilityAuthorityLockSession(bool throwOnDispose = false, bool blockOnDispose = false) : IAsyncDisposable
{
    internal int DisposeAttempts { get; private set; }

    internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        DisposeAttempts++;
        DisposeStarted.TrySetResult();
        if (blockOnDispose)
        {
            await ReleaseDisposal.Task;
        }
        if (throwOnDispose)
        {
            throw new IOException("Injected authority-session disposal failure.");
        }
    }
}
