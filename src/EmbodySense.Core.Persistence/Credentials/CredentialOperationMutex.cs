using System.Security.AccessControl;
using System.Security.Principal;

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
            mutex = CreateMutex(CredentialProviderTarget.MutexName(target));
            if (mutex is null)
            {
                return false;
            }

            var acquired = OperatingSystem.IsWindows()
                ? WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], _acquisitionTimeout) == 0
                : WaitPortable(mutex, cancellationToken);
            if (!acquired)
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

    private static bool WaitPortable(Mutex mutex, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < _acquisitionTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _acquisitionTimeout - started.Elapsed;
            if (mutex.WaitOne(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25)))
            {
                return true;
            }
        }

        return false;
    }

    private static Mutex? CreateMutex(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Mutex(false, name);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser is null)
        {
            return null;
        }

        // Credential Manager targets persist across Windows logon sessions, so the global namespace is paired with an explicit current-user DACL.
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new MutexAccessRule(currentUser, MutexRights.FullControl, AccessControlType.Allow));
        Mutex? mutex = null;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, name, out _, security);
            mutex.SetAccessControl(security);
            return mutex;
        }
        catch
        {
            mutex?.Dispose();
            throw;
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
