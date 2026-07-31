using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EmbodySense.Core.Common.Secrets.Redaction;

internal sealed class SensitiveRedactionPattern : IDisposable
{
    private char[]? _characters;

    public SensitiveRedactionPattern(char[] characters)
    {
        _characters = characters;
    }

    public int Length => _characters?.Length ?? 0;

    public ReadOnlySpan<char> Characters => _characters ?? [];

    public void Dispose()
    {
        if (_characters is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_characters.AsSpan()));
        _characters = null;
    }
}
