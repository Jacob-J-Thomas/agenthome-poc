using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using System.Text;

namespace EmbodySense.Core.Persistence.ContextualRoles;

/// <summary>Validates only the two established server-owned workspace instruction-source conventions.</summary>
/// <remarks>Source bytes are bounded and validated in-process, then discarded without crossing the persistence port.</remarks>
public sealed class WorkspaceContextualRoleInstructionSourceProbe : IContextualRoleInstructionSourceProbe
{
    /// <summary>Maximum UTF-8 bytes accepted from one registered instruction source.</summary>
    public const int MaximumInstructionSourceBytes = 64 * 1024;
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly WorkspacePaths _paths;

    /// <summary>Creates a source probe bound to one canonical workspace root.</summary>
    public WorkspaceContextualRoleInstructionSourceProbe(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<ContextualRoleInstructionSourceProbeResult> ProbeAsync(ContextualRoleInstructionSourceReference source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolve(source, out var path))
        {
            return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Unsupported);
        }

        try
        {
            var exactPath = path!;
            var directory = Path.GetDirectoryName(exactPath) ?? throw new InvalidOperationException("A registered contextual-role instruction source has no physical parent.");
            var bytes = await ContextualRoleArtifactPathGuard.ReadExternalBoundedFileAsync(directory, Path.GetFileName(exactPath), MaximumInstructionSourceBytes, cancellationToken);
            if (bytes is null)
            {
                return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Missing);
            }

            var content = _strictUtf8.GetString(bytes);
            return new ContextualRoleInstructionSourceProbeResult(string.IsNullOrWhiteSpace(content)
                ? ContextualRoleInstructionSourceProbeStatus.Ambiguous
                : ContextualRoleInstructionSourceProbeStatus.Ready);
        }
        catch (ContextualRoleInstructionSourceTooLargeException)
        {
            return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Oversized);
        }
        catch (DecoderFallbackException)
        {
            return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Ambiguous);
        }
        catch (InvalidOperationException)
        {
            return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Substituted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus.Unavailable);
        }
    }

    private bool TryResolve(ContextualRoleInstructionSourceReference? source, out string? path)
    {
        path = null;
        if (source?.Classification != ContextualRoleInstructionClassification.RoleInstruction)
        {
            return false;
        }

        if (source.Kind == ContextualRoleInstructionSourceKind.AgentsMarkdown && string.Equals(source.ReferenceId, "nearest-agents", StringComparison.Ordinal))
        {
            path = FindNearestAgentsCandidate() ?? Path.Combine(_paths.RootPath, WorkspaceInstructionLocator.FileName);
            return true;
        }

        if (source.Kind == ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown && string.Equals(source.ReferenceId, "role", StringComparison.Ordinal))
        {
            path = _paths.RolePath;
            return true;
        }

        return false;
    }

    private string? FindNearestAgentsCandidate()
    {
        var directory = new DirectoryInfo(_paths.RootPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, WorkspaceInstructionLocator.FileName);
            if (EntryExists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
