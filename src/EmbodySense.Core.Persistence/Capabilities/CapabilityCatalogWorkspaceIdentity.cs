using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Creates the stable identity used to bind server trust to one physical workspace directory.</summary>
public static class CapabilityCatalogWorkspaceIdentity
{
    /// <summary>Computes the schema-1 workspace identity from stable volume and directory identity.</summary>
    public static string Create(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        using var session = CapabilityCatalogPathSession.Open(root, comparison, createRoot: false) ?? throw new DirectoryNotFoundException("The capability catalog workspace root does not exist.");
        return CreateFromPhysicalIdentity(session.PhysicalIdentityMaterial);
    }

    internal static string CreateFromPhysicalIdentity(string physicalIdentityMaterial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIdentityMaterial);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("embodysense-capability-workspace-physical-v1\n" + physicalIdentityMaterial));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
