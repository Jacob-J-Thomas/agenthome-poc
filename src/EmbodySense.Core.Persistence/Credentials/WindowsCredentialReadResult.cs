using System.Security.Cryptography;
using EmbodySense.Core.Persistence.Credentials.Models;

namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class WindowsCredentialReadResult : IDisposable
{
    private byte[]? _value;

    private WindowsCredentialReadResult(WindowsCredentialStoreStatus status, byte[]? value)
    {
        Status = status;
        _value = value;
    }

    internal WindowsCredentialStoreStatus Status { get; }
    internal byte[] Value => _value ?? [];

    internal static WindowsCredentialReadResult Found(byte[] value) => new(WindowsCredentialStoreStatus.Success, value ?? throw new ArgumentNullException(nameof(value)));
    internal static WindowsCredentialReadResult Missing() => new(WindowsCredentialStoreStatus.Missing, null);
    internal static WindowsCredentialReadResult Failed(WindowsCredentialStoreStatus status)
    {
        return status is WindowsCredentialStoreStatus.Unavailable or WindowsCredentialStoreStatus.Corrupt ? new WindowsCredentialReadResult(status, null) : throw new ArgumentOutOfRangeException(nameof(status));
    }

    public void Dispose()
    {
        if (_value is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_value);
        _value = null;
    }
}
