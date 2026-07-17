using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Tools;

public sealed class ToolPermissionService : IToolPermissionService
{
    private readonly IDirectoryPermissionPolicy _policy;
    private readonly string _workspaceRootPath;
    private readonly string _policyHash;

    public ToolPermissionService(WorkspacePaths paths, IDirectoryPermissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;
        _workspaceRootPath = Path.GetFullPath(paths.RootPath);
        _policyHash = ComputePolicyHash(policy);
    }

    public ToolPermissionCheck Evaluate(ToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);

        var resolvedPath = ResolveTargetPath(request.TargetPath);

        if (!FileSystemPathComparer.IsWithinOrEqual(resolvedPath, _workspaceRootPath))
        {
            return new ToolPermissionCheck(
                resolvedPath,
                resolvedPath,
                MapOperation(request, resolvedPath),
                PermissionEvaluation.Denied(_workspaceRootPath, ToolPermissionDetails.OutsideWorkspaceRoot),
                _policyHash);
        }

        if (PathUsesReparsePoint(resolvedPath))
        {
            return new ToolPermissionCheck(
                resolvedPath,
                resolvedPath,
                MapOperation(request, resolvedPath),
                PermissionEvaluation.Denied(_workspaceRootPath, ToolPermissionDetails.ReparsePointPath),
                _policyHash);
        }

        var operation = MapOperation(request, resolvedPath);
        var permissionTargetPath = GetPermissionTargetPath(request.Command, resolvedPath);
        var evaluation = _policy.EvaluateDirectory(permissionTargetPath, operation);
        return new ToolPermissionCheck(resolvedPath, permissionTargetPath, operation, evaluation, _policyHash);
    }

    private static string ComputePolicyHash(IDirectoryPermissionPolicy policy)
    {
        var canonical = new StringBuilder();
        canonical.Append("document=").Append(policy.HasDocument ? '1' : '0').Append('\n');
        foreach (var entry in policy.Approved.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.RequiresApproval))
        {
            canonical.Append("allow\t").Append(entry.Path).Append('\t').Append(entry.RequiresApproval ? '1' : '0').Append('\t');
            canonical.AppendJoin(',', entry.Operations.OrderBy(operation => operation).Select(operation => ((int)operation).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            canonical.Append('\n');
        }

        foreach (var entry in policy.Denied.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            canonical.Append("deny\t").Append(entry.Path).Append("\t0\t");
            canonical.AppendJoin(',', entry.Operations.OrderBy(operation => operation).Select(operation => ((int)operation).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            canonical.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
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

}
