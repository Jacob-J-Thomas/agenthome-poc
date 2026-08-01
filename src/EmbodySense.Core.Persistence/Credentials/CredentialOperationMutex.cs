namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class CredentialOperationMutex : IDisposable
{
    private static readonly TimeSpan _acquisitionTimeout = TimeSpan.FromSeconds(5);
    private readonly Mutex _mutex;
    private bool _owned;

    private CredentialOperationMutex(Mutex mutex)
    {
        _mutex = mutex;
        _owned = true;
    }

    internal static bool TryAcquire(string target, CancellationToken cancellationToken, out CredentialOperationMutex? operationLock)
    {
        operationLock = null;
        Mutex? mutex = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            mutex = new Mutex(false, CredentialProviderTarget.MutexName(target));
            var waitResult = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], _acquisitionTimeout);
            if (waitResult != 0)
            {
                mutex.Dispose();
                return false;
            }

            operationLock = new CredentialOperationMutex(mutex);
            return true;
        }
        catch (AbandonedMutexException)
        {
            if (mutex is null)
            {
                return false;
            }

            operationLock = new CredentialOperationMutex(mutex);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (!_owned)
        {
            return;
        }

        _owned = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
