using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

internal static class AuthorityGrantEvidenceHash
{
    internal static string Compute(params string[] values)
    {
        var canonical = string.Join('\u001f', values.Select(value => value ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
