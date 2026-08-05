using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Persistence.Credentials;

internal static class CredentialProviderTarget
{
    private const string TargetPrefix = "EmbodySense:v1:";

    internal static string Derive(string workspaceId, CredentialReferenceId referenceId)
    {
        var workspaceBytes = Encoding.UTF8.GetBytes(workspaceId);
        var referenceBytes = Encoding.UTF8.GetBytes(referenceId.Value);
        var input = new byte[sizeof(int) + workspaceBytes.Length + referenceBytes.Length];
        var lengthBytes = BitConverter.GetBytes(workspaceBytes.Length);
        lengthBytes.CopyTo(input, 0);
        workspaceBytes.CopyTo(input, lengthBytes.Length);
        referenceBytes.CopyTo(input, lengthBytes.Length + workspaceBytes.Length);
        var digest = SHA256.HashData(input);

        CryptographicOperations.ZeroMemory(workspaceBytes);
        CryptographicOperations.ZeroMemory(referenceBytes);
        CryptographicOperations.ZeroMemory(input);
        var target = TargetPrefix + Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        return target;
    }

    internal static string MutexName(string target)
    {
        var platformPrefix = OperatingSystem.IsWindows() ? "Global\\" : string.Empty;
        return platformPrefix + "EmbodySense.Credentials.v1." + target[TargetPrefix.Length..];
    }
}
