using EmbodySense.Core.Common;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Validates that server-owned capability trust state is physically disjoint from governed workspace storage.</summary>
internal static class CapabilityCatalogTrustRootTopology
{
    /// <summary>Rejects equality or containment in either direction after lexical, link, and Windows physical-identity checks.</summary>
    /// <param name="workspaceRootPath">The governed workspace root.</param>
    /// <param name="trustRootPath">The server-owned capability trust root.</param>
    /// <exception cref="InvalidOperationException">Thrown when either root contains the other.</exception>
    public static void RequireDisjoint(string workspaceRootPath, string trustRootPath)
    {
        var workspaceRoot = Normalize(workspaceRootPath);
        var trustRoot = Normalize(trustRootPath);
        if (Overlaps(workspaceRoot, trustRoot))
        {
            throw OverlapException();
        }

        if (OperatingSystem.IsWindows())
        {
            if (WindowsPhysicalOverlap(workspaceRoot, trustRoot))
            {
                throw OverlapException();
            }

            return;
        }

        var physicalWorkspaceRoot = ResolveExistingLinks(workspaceRoot);
        var physicalTrustRoot = ResolveExistingLinks(trustRoot);
        if (Overlaps(physicalWorkspaceRoot, physicalTrustRoot))
        {
            throw OverlapException();
        }
    }

    private static bool WindowsPhysicalOverlap(string workspaceRoot, string trustRoot)
    {
        var workspaceAncestors = GetExistingWindowsAncestors(workspaceRoot);
        var trustAncestors = GetExistingWindowsAncestors(trustRoot);
        return workspaceAncestors.Any(workspace => trustAncestors.Any(trust => string.Equals(workspace.Identity, trust.Identity, StringComparison.Ordinal) && RelativeTailsOverlap(workspace.RelativeTail, trust.RelativeTail)));
    }

    private static IReadOnlyList<(string Identity, string RelativeTail)> GetExistingWindowsAncestors(string path)
    {
        var ancestors = new List<(string Identity, string RelativeTail)>();
        var tailSegments = new List<string>();
        var current = path;
        for (var depth = 0; depth <= 32; depth++)
        {
            if (CapabilityCatalogNativeFileSystem.TryGetExistingWindowsDirectoryIdentity(current, out var identity))
            {
                ancestors.Add((identity, string.Join(Path.DirectorySeparatorChar, tailSegments)));
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return ancestors;
            }

            tailSegments.Insert(0, Path.GetFileName(current));
            current = parent;
        }

        throw new IOException("Capability catalog root topology exceeded its bounded filesystem-link resolution depth.");
    }

    private static bool RelativeTailsOverlap(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return true;
        }

        return IsRelativeDescendant(first, second) || IsRelativeDescendant(second, first);
    }

    private static bool IsRelativeDescendant(string candidate, string ancestor)
    {
        return candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(ancestor + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExistingLinks(string path, int resolutionDepth = 0)
    {
        if (resolutionDepth > 32)
        {
            throw new IOException("Capability catalog root topology exceeded its bounded filesystem-link resolution depth.");
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("Capability catalog root topology could not be established safely.");
        }

        var relative = Path.GetRelativePath(root, path);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return Normalize(root);
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            if (!Directory.Exists(candidate))
            {
                for (; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                }

                return Normalize(current);
            }

            var directory = new DirectoryInfo(candidate);
            var target = directory.LinkTarget is null ? null : directory.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("Capability catalog root topology could not resolve a filesystem link safely.");
            if (target is not null)
            {
                var resolved = target.FullName;
                for (var remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    resolved = Path.Combine(resolved, segments[remaining]);
                }

                return ResolveExistingLinks(resolved, resolutionDepth + 1);
            }

            current = Normalize(candidate);
        }

        return Normalize(current);
    }

    private static bool Overlaps(string workspaceRoot, string trustRoot)
    {
        return FileSystemPathComparer.IsWithinOrEqual(workspaceRoot, trustRoot) || FileSystemPathComparer.IsWithinOrEqual(trustRoot, workspaceRoot);
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = @"\\" + fullPath[8..];
            }
            else if (fullPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath[4..];
            }

            fullPath = Path.GetFullPath(fullPath);
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static InvalidOperationException OverlapException()
    {
        return new InvalidOperationException("The server-owned capability catalog trust root must remain physically disjoint from the governed workspace.");
    }
}
