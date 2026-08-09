using System.Runtime.Versioning;
using EmbodySense.Core.Common;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Validates that server-owned capability trust state is physically disjoint from governed workspace storage.</summary>
internal static class CapabilityCatalogTrustRootTopology
{
    private const int MaximumWindowsIdentityProbes = 32;

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

    [SupportedOSPlatform("windows")]
    private static bool WindowsPhysicalOverlap(string workspaceRoot, string trustRoot)
    {
        var workspaceAncestors = GetExistingWindowsAncestors(workspaceRoot);
        var trustAncestors = GetExistingWindowsAncestors(trustRoot);
        return workspaceAncestors.Any(workspace => trustAncestors.Any(trust => string.Equals(workspace.Identity, trust.Identity, StringComparison.Ordinal) && RelativeTailsOverlap(workspace.RelativeTail, trust.RelativeTail)));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<(string Identity, string RelativeTail)> GetExistingWindowsAncestors(string path)
    {
        var tailSegments = new List<string>();
        var current = path;
        var remainingProbes = MaximumWindowsIdentityProbes;
        for (; ; )
        {
            RequireSafeTopology(remainingProbes > 0, "Capability catalog root topology exceeded its bounded filesystem-link resolution depth.");
            remainingProbes--;
            if (CapabilityCatalogNativeFileSystem.TryGetExistingWindowsDirectoryIdentity(current, out var identity, out var finalPath))
            {
                return GetCanonicalWindowsAncestors(finalPath, identity, tailSegments, remainingProbes);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            tailSegments.Insert(0, Path.GetFileName(current));
            current = parent;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<(string Identity, string RelativeTail)> GetCanonicalWindowsAncestors(string finalPath, string identity, List<string> tailSegments, int remainingProbes)
    {
        var ancestors = new List<(string Identity, string RelativeTail)> { (identity, string.Join(Path.DirectorySeparatorChar, tailSegments)) };
        var current = Normalize(finalPath);
        for (; ; )
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return ancestors;
            }

            tailSegments.Insert(0, Path.GetFileName(current));
            current = parent;
            RequireSafeTopology(remainingProbes > 0, "Capability catalog root topology exceeded its bounded filesystem-link resolution depth.");
            remainingProbes--;
            var parentResolved = CapabilityCatalogNativeFileSystem.TryGetExistingWindowsDirectoryIdentity(current, out var parentIdentity, out _);
            RequireSafeTopology(parentResolved, "Capability catalog root topology could not resolve an existing directory safely.");

            ancestors.Add((parentIdentity, string.Join(Path.DirectorySeparatorChar, tailSegments)));
        }
    }

    private static void RequireSafeTopology(bool condition, string failureMessage)
    {
        if (!condition)
        {
            throw new IOException(failureMessage);
        }
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
            else if (fullPath.Length >= 7
                && fullPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                && char.IsAsciiLetter(fullPath[4])
                && fullPath[5] == ':'
                && (fullPath[6] == Path.DirectorySeparatorChar || fullPath[6] == Path.AltDirectorySeparatorChar))
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
