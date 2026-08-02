using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Completes the durable metadata barrier after an atomic capability-catalog replacement.</summary>
/// <remarks>
/// Implementations receive a borrowed parent-directory handle and must not dispose it. Trusted hosts may inject a
/// platform-specific implementation when the built-in Windows, Linux, or macOS barrier is not appropriate.
/// </remarks>
public interface ICapabilityCatalogDurabilityBarrier
{
    /// <summary>Runs after a Windows staging directory is retained and validated but before its handle-based move.</summary>
    /// <param name="stagingPath">The randomized staging path.</param>
    /// <param name="destinationPath">The final directory path.</param>
    void BeforeDirectoryMove(string stagingPath, string destinationPath);

    /// <summary>Runs after the handle-based move and flush but before the final path is reopened and identity-checked.</summary>
    /// <param name="stagingPath">The former randomized staging path.</param>
    /// <param name="destinationPath">The final directory path.</param>
    void AfterDirectoryMove(string stagingPath, string destinationPath);

    /// <summary>Completes only after a newly created directory entry is durably committed in its parent.</summary>
    /// <param name="directoryPath">The canonical path of the newly created directory.</param>
    /// <param name="parentDirectory">The retained no-follow handle for the new directory's parent.</param>
    void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory);

    /// <summary>Completes only after the renamed destination and its parent metadata are durably committed.</summary>
    /// <param name="destinationPath">The canonical destination path after the atomic rename.</param>
    /// <param name="parentDirectory">The retained no-follow handle for the destination parent directory.</param>
    /// <returns>A value task that completes after the durability boundary is crossed.</returns>
    ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory);
}
