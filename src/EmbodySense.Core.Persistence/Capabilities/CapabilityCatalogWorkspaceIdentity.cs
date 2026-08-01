using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Creates the stable identity used to bind server trust to one physical workspace directory.</summary>
public static class CapabilityCatalogWorkspaceIdentity
{
    internal const string LinuxUnsupportedMessage = "Capability catalog Linux workspace identity is unsupported because no non-owner-writable directory-lifetime identity is available.";
    private const uint MacVolumeCapabilityPersistentObjectIds = 0x00000001;

    /// <summary>Computes the schema-1 workspace identity from stable volume and directory identity.</summary>
    /// <remarks>Windows and macOS require server-observed, non-recycled identity evidence. Linux fails closed until its platform integration supplies an accepted non-owner-writable directory-lifetime identity.</remarks>
    /// <param name="workspaceRoot">The existing workspace root to identify through a retained no-follow directory handle.</param>
    /// <returns>The canonical workspace identity digest used to bind server-owned trust.</returns>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot" /> is null, empty, or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="workspaceRoot" /> does not exist.</exception>
    /// <exception cref="IOException">The physical identity cannot be read or does not contain a stable lifetime discriminator.</exception>
    /// <exception cref="PlatformNotSupportedException">The current platform does not expose an accepted non-owner-writable workspace lifetime identity.</exception>
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

    /// <summary>Requires a successful native physical-identity read without trusting any returned identity fields.</summary>
    /// <remarks>This deterministic validation seam does not perform native I/O. Production passes only the result and last-error code captured immediately from a retained directory handle.</remarks>
    /// <param name="nativeResult">The native operation result, where zero indicates success.</param>
    /// <param name="nativeError">The native last-error code captured for a failed operation.</param>
    /// <exception cref="IOException"><paramref name="nativeResult" /> indicates failure.</exception>
    public static void RequireNativePhysicalIdentityRead(int nativeResult, int nativeError)
    {
        if (nativeResult != 0)
        {
            throw CapabilityCatalogNativeFileSystem.NativeIOException("The capability catalog workspace physical identity could not be read", nativeError);
        }
    }

    /// <summary>Determines whether macOS volume-capability evidence proves persistent, non-recycled object identifiers.</summary>
    /// <remarks>This deterministic ABI validation does not perform native I/O or trust caller-supplied evidence. Production supplies the capability buffer returned for a retained directory handle.</remarks>
    /// <param name="nativeResult">The <c>fgetattrlist</c> result, where zero indicates success.</param>
    /// <param name="nativeError">The native last-error code captured for a failed operation.</param>
    /// <param name="returnedBufferLength">The capability buffer length reported by the kernel.</param>
    /// <param name="requiredBufferLength">The complete capability structure size required by the current ABI.</param>
    /// <param name="validFormatCapabilities">The kernel validity mask for format capability bits.</param>
    /// <param name="formatCapabilities">The enabled filesystem format capability bits.</param>
    /// <returns><see langword="true" /> only when the complete buffer marks <c>VOL_CAP_FMT_PERSISTENTOBJECTIDS</c> as both valid and enabled.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requiredBufferLength" /> is zero.</exception>
    /// <exception cref="IOException">The native capability read failed.</exception>
    public static bool MacVolumeCapabilitiesProveNonRecycledObjectIdentity(int nativeResult, int nativeError, uint returnedBufferLength, uint requiredBufferLength, uint validFormatCapabilities, uint formatCapabilities)
    {
        ArgumentOutOfRangeException.ThrowIfZero(requiredBufferLength);
        if (nativeResult != 0)
        {
            throw CapabilityCatalogNativeFileSystem.NativeIOException("The capability catalog workspace volume identity capability could not be read", nativeError);
        }

        return returnedBufferLength >= requiredBufferLength && (validFormatCapabilities & MacVolumeCapabilityPersistentObjectIds) != 0 && (formatCapabilities & MacVolumeCapabilityPersistentObjectIds) != 0;
    }

    /// <summary>Creates canonical Unix physical identity material after requiring a directory lifetime discriminator.</summary>
    /// <remarks>This deterministic validation seam keeps filesystem availability failures testable without treating a path string as trusted identity evidence.</remarks>
    /// <param name="platform">The Unix platform identifier. macOS is supported; Linux fails closed until a non-owner-writable lifetime identity is proven.</param>
    /// <param name="deviceMajor">The server-observed filesystem device major identifier.</param>
    /// <param name="deviceMinor">The server-observed filesystem device minor identifier.</param>
    /// <param name="inode">The server-observed stable directory inode, or <see langword="null" /> when unavailable.</param>
    /// <param name="directoryGeneration">The server-observed filesystem generation for this exact inode lifetime, or <see langword="null" /> when unavailable.</param>
    /// <param name="birthTimeSeconds">The server-observed directory creation timestamp seconds, or <see langword="null" /> when unavailable.</param>
    /// <param name="birthTimeNanoseconds">The server-observed directory creation timestamp nanoseconds, or <see langword="null" /> when unavailable.</param>
    /// <param name="inodeIsNonRecycled">Whether the retained directory's volume proves that object identifiers are persistent and not recycled.</param>
    /// <returns>Opaque physical identity material that includes the directory lifetime discriminator.</returns>
    /// <exception cref="ArgumentException"><paramref name="platform" /> is not a recognized Unix platform.</exception>
    /// <exception cref="PlatformNotSupportedException"><paramref name="platform" /> is Linux, whose current integration does not expose an accepted non-owner-writable lifetime identity.</exception>
    /// <exception cref="IOException">The filesystem did not provide a stable inode or lifetime discriminator.</exception>
    public static string CreateUnixPhysicalIdentityMaterial(string platform, uint deviceMajor, uint deviceMinor, ulong? inode, uint? directoryGeneration, long? birthTimeSeconds, long? birthTimeNanoseconds, bool inodeIsNonRecycled = false)
    {
        if (platform == "linux")
        {
            throw new PlatformNotSupportedException(LinuxUnsupportedMessage);
        }

        if (platform != "macos")
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
        return $"macos:{deviceMajor:x8}:{inode:x16}:{lifetimeIdentity}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x16}";
    }
}
