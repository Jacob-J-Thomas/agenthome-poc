using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EmbodySense.Core.Common.Secrets.Redaction;

internal sealed class SensitiveRedactionPattern : IDisposable
{
    private char[]? _characters;

    public SensitiveRedactionPattern(char[] characters, bool matchesPercentHexCaseInsensitively = false)
    {
        _characters = characters;
        MatchesPercentHexCaseInsensitively = matchesPercentHexCaseInsensitively;
    }

    public int Length => _characters?.Length ?? 0;

    public ReadOnlySpan<char> Characters => _characters ?? [];

    public bool MatchesPercentHexCaseInsensitively { get; }

    public bool MatchesCharacter(int index, char value)
    {
        var characters = _characters;
        if (characters is null || index < 0 || index >= characters.Length)
        {
            return false;
        }

        var expected = characters[index];
        if (expected == value)
        {
            return true;
        }

        if (!MatchesPercentHexCaseInsensitively || !IsPercentHexPosition(characters, index) || !IsHex(value))
        {
            return false;
        }

        return char.ToUpperInvariant(value) == char.ToUpperInvariant(expected);
    }

    public void Dispose()
    {
        if (_characters is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_characters.AsSpan()));
        _characters = null;
    }

    private static bool IsPercentHexPosition(ReadOnlySpan<char> characters, int index)
    {
        return index >= 1 && characters[index - 1] == '%' || index >= 2 && characters[index - 2] == '%';
    }

    private static bool IsHex(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }
}
