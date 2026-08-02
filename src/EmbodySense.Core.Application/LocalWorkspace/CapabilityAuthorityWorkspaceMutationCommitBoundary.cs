using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common;
using EmbodySense.Core.Common.Workspace;
using System.Text;

namespace EmbodySense.Core.Application.LocalWorkspace;

/// <summary>Fences exact filesystem commits whose target trees overlap the configured skill-authority root.</summary>
/// <remarks>macOS overlap checks conservatively normalize Unicode and ignore case at this authority boundary without broadening the global permission-policy path comparison.</remarks>
public sealed class CapabilityAuthorityWorkspaceMutationCommitBoundary : IWorkspaceMutationCommitBoundary
{
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly string _skillsRootPath;

    /// <summary>Creates a workspace mutation boundary over the shared capability-authority transaction.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="authorityTransaction">The transaction also used by capability lifecycle, catalog, artifact, and dependent-capture operations.</param>
    public CapabilityAuthorityWorkspaceMutationCommitBoundary(WorkspacePaths paths, ICapabilityAuthorityTransaction authorityTransaction)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(authorityTransaction);

        _skillsRootPath = Path.GetFullPath(paths.SkillsPath);
        _authorityTransaction = authorityTransaction;
    }

    /// <inheritdoc />
    public Task<TResult> ExecuteAsync<TResult>(IReadOnlyCollection<string> affectedPaths, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affectedPaths);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        if (affectedPaths.Count == 0)
        {
            throw new ArgumentException("A workspace mutation must identify at least one affected path.", nameof(affectedPaths));
        }

        var requiresCapabilityAuthority = false;
        foreach (var affectedPath in affectedPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(affectedPath);
            var normalizedPath = Path.GetFullPath(affectedPath);
            requiresCapabilityAuthority |= IsWithinOrEqualAuthorityPath(normalizedPath, _skillsRootPath)
                || IsWithinOrEqualAuthorityPath(_skillsRootPath, normalizedPath);
        }

        return requiresCapabilityAuthority ? _authorityTransaction.ExecuteAsync(commit, cancellationToken) : commit(cancellationToken);
    }

    private static bool IsWithinOrEqualAuthorityPath(string candidatePath, string rootPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return FileSystemPathComparer.IsWithinOrEqual(candidatePath, rootPath);
        }

        var normalizedCandidatePath = EnsureTrailingSeparator(candidatePath.Normalize(NormalizationForm.FormC));
        var normalizedRootPath = EnsureTrailingSeparator(rootPath.Normalize(NormalizationForm.FormC));
        return normalizedCandidatePath.StartsWith(normalizedRootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }
}
