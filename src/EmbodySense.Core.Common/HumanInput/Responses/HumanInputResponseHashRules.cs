using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.HumanInput.Responses;

internal static class HumanInputResponseHashRules
{
    internal static bool IsSha256(string? value) => value is { Length: HumanInputLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool FixedEquals(string? left, string? right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.ASCII.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
