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

    /// <summary>Creates canonical Unix physical identity material after requiring a directory lifetime discriminator.</summary>
    /// <remarks>This deterministic validation seam keeps filesystem availability failures testable without treating a path string as trusted identity evidence.</remarks>
    /// <param name="platform">The supported Unix platform identifier: <c>linux</c> or <c>macos</c>.</param>
    /// <param name="deviceMajor">The server-observed filesystem device major identifier.</param>
    /// <param name="deviceMinor">The server-observed filesystem device minor identifier.</param>
    /// <param name="inode">The server-observed stable directory inode, or <see langword="null" /> when unavailable.</param>
    /// <param name="directoryGeneration">The server-observed filesystem generation for this exact inode lifetime, or <see langword="null" /> when unavailable.</param>
    /// <param name="birthTimeSeconds">The server-observed directory creation timestamp seconds, or <see langword="null" /> when unavailable.</param>
    /// <param name="birthTimeNanoseconds">The server-observed directory creation timestamp nanoseconds, or <see langword="null" /> when unavailable.</param>
    /// <param name="inodeIsNonRecycled">Whether the retained directory's volume proves that object identifiers are persistent and not recycled.</param>
    /// <returns>Opaque physical identity material that includes the directory lifetime discriminator.</returns>
    /// <exception cref="ArgumentException"><paramref name="platform" /> is unsupported.</exception>
    /// <exception cref="IOException">The filesystem did not provide a stable inode or lifetime discriminator.</exception>
    public static string CreateUnixPhysicalIdentityMaterial(string platform, uint deviceMajor, uint deviceMinor, ulong? inode, uint? directoryGeneration, long? birthTimeSeconds, long? birthTimeNanoseconds, bool inodeIsNonRecycled = false)
    {
        if (platform is not ("linux" or "macos"))
        {
            throw new ArgumentException("Capability catalog Unix workspace identity requires the linux or macos platform identifier.", nameof(platform));
        }

        if (inode is null)
        {
            throw new IOException("The capability catalog workspace physical identity does not expose a stable inode.");
        }

        if ((directoryGeneration is null || directoryGeneration == 0) && !inodeIsNonRecycled)
        {
            throw new IOException("The capability catalog workspace filesystem exposes neither an inode generation nor a non-recycled object identity.");
        }

        if (birthTimeSeconds is null || birthTimeNanoseconds is null || birthTimeSeconds == 0 && birthTimeNanoseconds == 0)
        {
            throw new IOException("The capability catalog workspace filesystem does not expose the required lifetime discriminator.");
        }

        var lifetimeIdentity = directoryGeneration is not null and not 0 ? $"generation-{directoryGeneration:x8}" : "nonrecycled-inode";
        return platform == "linux"
            ? $"linux:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{lifetimeIdentity}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x8}"
            : $"macos:{deviceMajor:x8}:{inode:x16}:{lifetimeIdentity}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x16}";
    }
}
