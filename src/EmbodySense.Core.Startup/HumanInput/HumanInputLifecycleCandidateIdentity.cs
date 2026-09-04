using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Creates deterministic opaque identities for repeatable lifecycle candidate preparation.</summary>
internal static class HumanInputLifecycleCandidateIdentity
{
    internal static string Digest(params string?[] parts)
    {
        var material = string.Join("\u001f", parts.Select(part => part ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    internal static string RequestVersion(string kind, string operationId, string requestId, string requestHash, string intent, int discriminator = 0)
        => $"version-{kind.ToLowerInvariant()}-{Digest(kind, operationId, requestId, requestHash, intent, discriminator.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
}
