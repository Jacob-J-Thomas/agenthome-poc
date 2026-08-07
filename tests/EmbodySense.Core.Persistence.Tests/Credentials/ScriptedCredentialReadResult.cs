using System.Security.Cryptography;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class ScriptedCredentialReadResult : IDisposable
{
    private byte[]? _value;

    private ScriptedCredentialReadResult(ScriptedCredentialStoreStatus status, byte[]? value)
    {
        Status = status;
        _value = value;
    }

    internal ScriptedCredentialStoreStatus Status { get; }
    internal byte[] Value => _value ?? [];

    internal static ScriptedCredentialReadResult Found(byte[] value) => new(ScriptedCredentialStoreStatus.Success, value ?? throw new ArgumentNullException(nameof(value)));
    internal static ScriptedCredentialReadResult Missing() => new(ScriptedCredentialStoreStatus.Missing, null);
    internal static ScriptedCredentialReadResult Failed(ScriptedCredentialStoreStatus status)
    {
        return status is ScriptedCredentialStoreStatus.Unavailable or ScriptedCredentialStoreStatus.Corrupt ? new ScriptedCredentialReadResult(status, null) : throw new ArgumentOutOfRangeException(nameof(status));
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
