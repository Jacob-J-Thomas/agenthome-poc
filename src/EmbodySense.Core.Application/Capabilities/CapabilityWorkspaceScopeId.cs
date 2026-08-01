using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Creates a non-secret stable workspace binding for capability-admission evidence.</summary>
public static class CapabilityWorkspaceScopeId
{
    /// <summary>Hashes the normalized physical workspace path without persisting the path itself.</summary>
    public static string Create(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)).Normalize(NormalizationForm.FormC);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return "workspace-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
