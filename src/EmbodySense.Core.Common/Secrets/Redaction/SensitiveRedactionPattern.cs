using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EmbodySense.Core.Common.Secrets.Redaction;

internal sealed class SensitiveRedactionPattern : IDisposable
{
    private char[]? _characters;

    public SensitiveRedactionPattern(char[] characters, bool matchesPercentHexCaseInsensitively = false, bool matchesEscapedUnreservedCharacters = false)
    {
        _characters = characters;
        MatchesPercentHexCaseInsensitively = matchesPercentHexCaseInsensitively;
        MatchesEscapedUnreservedCharacters = matchesEscapedUnreservedCharacters;
    }

    public int Length => _characters?.Length ?? 0;

    public ReadOnlySpan<char> Characters => _characters ?? [];

    public bool MatchesPercentHexCaseInsensitively { get; }

    public bool MatchesEscapedUnreservedCharacters { get; }

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

    public bool TryMatch(ReadOnlySpan<char> input, int inputIndex, ref int workUnitCount, int maximumWorkUnits, out int matchedLength, out bool workLimitExceeded)
    {
        matchedLength = 0;
        workLimitExceeded = false;
        var characters = _characters;
        if (characters is null || inputIndex < 0 || inputIndex > input.Length)
        {
            return false;
        }

        var inputOffset = 0;
        for (var patternIndex = 0; patternIndex < characters.Length; patternIndex++)
        {
            var expected = characters[patternIndex];
            if (MatchesEscapedUnreservedCharacters
                && !IsPercentHexPosition(characters, patternIndex)
                && IsUnreserved(expected)
                && IsPercentEscape(input, inputIndex + inputOffset, expected, ref workUnitCount, maximumWorkUnits, out workLimitExceeded))
            {
                inputOffset += 3;
                continue;
            }

            if (workLimitExceeded || !TryConsumeWork(ref workUnitCount, maximumWorkUnits, out workLimitExceeded))
            {
                return false;
            }

            if (inputIndex + inputOffset >= input.Length || !MatchesCharacter(patternIndex, input[inputIndex + inputOffset]))
            {
                return false;
            }

            inputOffset++;
        }

        matchedLength = inputOffset;
        return true;
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

    private static bool IsUnreserved(char value)
    {
        return value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '.' or '_' or '~';
    }

    private static bool IsPercentEscape(ReadOnlySpan<char> input, int index, char expected, ref int workUnitCount, int maximumWorkUnits, out bool workLimitExceeded)
    {
        if (!TryConsumeWork(ref workUnitCount, maximumWorkUnits, out workLimitExceeded))
        {
            return false;
        }

        if (index + 3 > input.Length || input[index] != '%')
        {
            return false;
        }

        var expectedValue = (byte)expected;
        return MatchesEscapedNibble(input[index + 1], expectedValue >> 4, ref workUnitCount, maximumWorkUnits, out workLimitExceeded)
            && MatchesEscapedNibble(input[index + 2], expectedValue & 0x0f, ref workUnitCount, maximumWorkUnits, out workLimitExceeded);
    }

    private static bool MatchesEscapedNibble(char actual, int expected, ref int workUnitCount, int maximumWorkUnits, out bool workLimitExceeded)
    {
        if (!TryConsumeWork(ref workUnitCount, maximumWorkUnits, out workLimitExceeded))
        {
            return false;
        }

        var expectedCharacter = expected < 10 ? (char)('0' + expected) : (char)('A' + expected - 10);
        return char.ToUpperInvariant(actual) == expectedCharacter;
    }

    private static bool TryConsumeWork(ref int workUnitCount, int maximumWorkUnits, out bool workLimitExceeded)
    {
        workUnitCount++;
        workLimitExceeded = workUnitCount > maximumWorkUnits;
        return !workLimitExceeded;
    }
}
