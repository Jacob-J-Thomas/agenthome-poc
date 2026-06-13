using EmbodySense.Core.Permissions;
using EmbodySense.Core.Permissions.Models;
using EmbodySense.Core.Tools.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Tools;

public sealed class ToolPermissionService
{
    private readonly DirectoryPermissionPolicy _policy;
    private readonly string _workspaceRootPath;

    public ToolPermissionService(WorkspacePaths paths, DirectoryPermissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;
        _workspaceRootPath = Path.GetFullPath(paths.RootPath);
    }

    public ToolPermissionCheck Evaluate(ToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);

        var resolvedPath = ResolveTargetPath(request.TargetPath);

        if (!IsWithinOrEqual(resolvedPath, _workspaceRootPath))
        {
            return new ToolPermissionCheck(
                resolvedPath,
                resolvedPath,
                MapOperation(request, resolvedPath),
                PermissionEvaluation.Denied(_workspaceRootPath, ToolPermissionDetails.OutsideWorkspaceRoot));
        }

        if (PathUsesReparsePoint(resolvedPath))
        {
            return new ToolPermissionCheck(
                resolvedPath,
                resolvedPath,
                MapOperation(request, resolvedPath),
                PermissionEvaluation.Denied(_workspaceRootPath, ToolPermissionDetails.ReparsePointPath));
        }

        var operation = MapOperation(request, resolvedPath);
        var permissionTargetPath = GetPermissionTargetPath(request.Command, resolvedPath);
        var evaluation = _policy.EvaluateDirectory(permissionTargetPath, operation);
        return new ToolPermissionCheck(resolvedPath, permissionTargetPath, operation, evaluation);
    }

    private string ResolveTargetPath(string targetPath)
    {
        var effectivePath = Path.IsPathRooted(targetPath) ? targetPath : Path.Combine(_workspaceRootPath, targetPath);
        return Path.GetFullPath(effectivePath);
    }

    private static FileSystemOperation MapOperation(ToolRequest request, string resolvedPath)
    {
        return request.Command switch
        {
            ToolCommand.List => FileSystemOperation.List,
            ToolCommand.Read => FileSystemOperation.Read,
            ToolCommand.Search => FileSystemOperation.Read,
            ToolCommand.Append => File.Exists(resolvedPath) ? FileSystemOperation.Append : FileSystemOperation.Create,
            ToolCommand.Write => File.Exists(resolvedPath) ? FileSystemOperation.Modify : FileSystemOperation.Create,
            ToolCommand.Delete => FileSystemOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "Unsupported tool command.")
        };
    }

    private static string GetPermissionTargetPath(ToolCommand command, string resolvedPath)
    {
        var targetIsDirectory = Directory.Exists(resolvedPath);

        if (command == ToolCommand.List || targetIsDirectory && (command == ToolCommand.Search || command == ToolCommand.Delete))
        {
            return resolvedPath;
        }

        return Path.GetDirectoryName(resolvedPath) ?? resolvedPath;
    }

    private bool PathUsesReparsePoint(string resolvedPath)
    {
        var relativePath = Path.GetRelativePath(_workspaceRootPath, resolvedPath);
        if (relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = _workspaceRootPath;

        if (HasReparsePoint(candidate))
        {
            return true;
        }

        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            candidate = Path.Combine(candidate, segment);

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return false;
            }

            if (HasReparsePoint(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsWithinOrEqual(string candidatePath, string rootPath)
    {
        var normalizedCandidatePath = EnsureTrailingSeparator(candidatePath);
        var normalizedRootPath = EnsureTrailingSeparator(rootPath);
        return normalizedCandidatePath.StartsWith(normalizedRootPath, GetPathComparison());
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
