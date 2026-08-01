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

    /// <summary>Computes the canonical workspace identity digest from opaque physical directory identity material.</summary>
    /// <remarks>This pure mapping does not inspect a directory or establish that its input is trustworthy. Production callers source the material from a retained directory handle; the public mapping also provides a deterministic lifetime-regression seam.</remarks>
    /// <param name="physicalIdentityMaterial">Opaque physical directory identity material, including a lifetime discriminator.</param>
    /// <returns>The canonical workspace identity digest used to bind server-owned trust.</returns>
    /// <exception cref="ArgumentException"><paramref name="physicalIdentityMaterial" /> is null, empty, or whitespace.</exception>
    public static string CreateFromPhysicalIdentity(string physicalIdentityMaterial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIdentityMaterial);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("embodysense-capability-workspace-physical-v1\n" + physicalIdentityMaterial));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
