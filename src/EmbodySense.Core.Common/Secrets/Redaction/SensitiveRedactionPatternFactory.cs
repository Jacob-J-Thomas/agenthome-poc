using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace EmbodySense.Core.Common.Secrets.Redaction;

internal static class SensitiveRedactionPatternFactory
{
    private const string Rfc3986SafePunctuation = "-._~";
    private const string DotNetUriSafePunctuation = "-._~!'()*";
    private const string FormSafePunctuation = "-._!*()";
    private static readonly Encoding _replacementUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static void AddSupportedPatterns(List<SensitiveRedactionPattern> patterns, ReadOnlySpan<char> value)
    {
        AddOwnedDistinct(patterns, value.ToArray(), matchesPercentHexCaseInsensitively: false);

        var utf8 = new byte[_replacementUtf8.GetByteCount(value)];
        try
        {
            _replacementUtf8.GetBytes(value, utf8);
            AddOwnedDistinct(patterns, EncodePercent(utf8, Rfc3986SafePunctuation, formStyle: false), matchesPercentHexCaseInsensitively: true);
            AddOwnedDistinct(patterns, EncodePercent(utf8, DotNetUriSafePunctuation, formStyle: false), matchesPercentHexCaseInsensitively: true);
            AddOwnedDistinct(patterns, EncodePercent(utf8, FormSafePunctuation, formStyle: true), matchesPercentHexCaseInsensitively: true);

            var base64 = new char[((utf8.Length + 2) / 3) * 4];
            var callerOwnsBase64 = true;
            try
            {
                Convert.TryToBase64Chars(utf8, base64, out _);
                callerOwnsBase64 = false;
                AddOwnedDistinct(patterns, base64, matchesPercentHexCaseInsensitively: false);
            }
            finally
            {
                if (callerOwnsBase64)
                {
                    Zero(base64);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static void AddOwnedDistinct(List<SensitiveRedactionPattern> patterns, char[] candidate, bool matchesPercentHexCaseInsensitively)
    {
        var callerOwnsCandidate = true;
        try
        {
            foreach (var pattern in patterns)
            {
                if (pattern.MatchesPercentHexCaseInsensitively != matchesPercentHexCaseInsensitively || !pattern.Characters.SequenceEqual(candidate))
                {
                    continue;
                }

                return;
            }

            patterns.Add(new SensitiveRedactionPattern(candidate, matchesPercentHexCaseInsensitively));
            callerOwnsCandidate = false;
        }
        finally
        {
            if (callerOwnsCandidate)
            {
                Zero(candidate);
            }
        }
    }

    private static char[] EncodePercent(ReadOnlySpan<byte> value, string safePunctuation, bool formStyle)
    {
        var length = 0;
        foreach (var item in value)
        {
            length += IsEncodingSafe(item, safePunctuation) || (formStyle && item == (byte)' ') ? 1 : 3;
        }

        var result = new char[length];
        var index = 0;
        foreach (var item in value)
        {
            if (IsEncodingSafe(item, safePunctuation))
            {
                result[index++] = (char)item;
            }
            else if (formStyle && item == (byte)' ')
            {
                result[index++] = '+';
            }
            else
            {
                result[index++] = '%';
                result[index++] = ToHex(item >> 4);
                result[index++] = ToHex(item & 0x0f);
            }
        }

        return result;
    }

    private static bool IsEncodingSafe(byte value, string safePunctuation)
    {
        return (value >= (byte)'a' && value <= (byte)'z')
            || (value >= (byte)'A' && value <= (byte)'Z')
            || (value >= (byte)'0' && value <= (byte)'9')
            || safePunctuation.Contains((char)value, StringComparison.Ordinal);
    }

    private static char ToHex(int value)
    {
        if (value < 10)
        {
            return (char)('0' + value);
        }

        return (char)('A' + value - 10);
    }

    private static void Zero(char[] value)
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
