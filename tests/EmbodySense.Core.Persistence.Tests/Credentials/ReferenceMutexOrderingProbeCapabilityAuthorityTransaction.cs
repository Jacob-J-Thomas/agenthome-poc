using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Persistence.Tests.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class ReferenceMutexOrderingProbeCapabilityAuthorityTransaction(string workspaceId, CredentialReferenceId referenceId) : ICapabilityAuthorityTransaction
{
    private readonly string _mutexName = CreateMutexName(workspaceId, referenceId);
    private int _referenceMutexWasHeldBeforeAuthority;

    internal bool ReferenceMutexWasHeldBeforeAuthority => Volatile.Read(ref _referenceMutexWasHeldBeforeAuthority) != 0;

    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        using var probeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var acquired = await Task.Run(() => TryAcquireAndRelease(probeCancellation.Token), CancellationToken.None);
        if (!acquired)
        {
            Interlocked.Exchange(ref _referenceMutexWasHeldBeforeAuthority, 1);
            return null;
        }

        return await validator(cancellationToken) ? new StubCapabilityAuthorityLease() : null;
    }

    private bool TryAcquireAndRelease(CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, _mutexName);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mutex.WaitOne(TimeSpan.FromMilliseconds(25)))
            {
                continue;
            }

            try
            {
                return true;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        return false;
    }

    private static string CreateMutexName(string workspaceId, CredentialReferenceId referenceId)
    {
        var workspaceBytes = Encoding.UTF8.GetBytes(workspaceId);
        var referenceBytes = Encoding.UTF8.GetBytes(referenceId.Value);
        var input = new byte[sizeof(int) + workspaceBytes.Length + referenceBytes.Length];
        BitConverter.GetBytes(workspaceBytes.Length).CopyTo(input, 0);
        workspaceBytes.CopyTo(input, sizeof(int));
        referenceBytes.CopyTo(input, sizeof(int) + workspaceBytes.Length);
        var digest = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(workspaceBytes);
        CryptographicOperations.ZeroMemory(referenceBytes);
        CryptographicOperations.ZeroMemory(input);
        var prefix = OperatingSystem.IsWindows() ? "Global\\" : string.Empty;
        var mutexName = prefix + "EmbodySense.Credentials.v1." + Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        return mutexName;
    }
}
