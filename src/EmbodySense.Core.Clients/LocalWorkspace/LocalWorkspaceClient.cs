using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Clients.LocalWorkspace;

public sealed class LocalWorkspaceClient : ILocalWorkspaceClient
{
    private readonly WorkspacePaths _paths;

    public LocalWorkspaceClient(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    public Task<LocalWorkspaceResult> ListAsync(string resolvedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(resolvedPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {resolvedPath}");
        }

        var entries = Directory.EnumerateFileSystemEntries(resolvedPath)
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => Directory.Exists(path) ? Path.GetFileName(path) + Path.DirectorySeparatorChar : Path.GetFileName(path))
            .ToList();
        var text = entries.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        return Task.FromResult(new LocalWorkspaceResult(text, new Dictionary<string, object?> { ["entry_count"] = entries.Count }));
    }

    public async Task<LocalWorkspaceResult> ReadAsync(string resolvedPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {resolvedPath}", resolvedPath);
        }

        var text = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        return new LocalWorkspaceResult(text, new Dictionary<string, object?> { ["character_count"] = text.Length });
    }

    public async Task<LocalWorkspaceResult> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new IOException("Search requires a non-empty pattern.");
        }

        var matches = new List<string>();

        if (File.Exists(resolvedPath))
        {
            await SearchFileAsync(resolvedPath, pattern, matches, cancellationToken);
        }
        else if (Directory.Exists(resolvedPath))
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };

            foreach (var file in Directory.EnumerateFiles(resolvedPath, "*", options).Order(StringComparer.OrdinalIgnoreCase))
            {
                await SearchFileAsync(file, pattern, matches, cancellationToken);
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Search target not found: {resolvedPath}");
        }

        var text = matches.Count == 0 ? "(no matches)" : string.Join(Environment.NewLine, matches);
        return new LocalWorkspaceResult(text, new Dictionary<string, object?> { ["match_count"] = matches.Count, ["pattern_length"] = pattern.Length });
    }

    public async Task<LocalWorkspaceResult> AppendAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new IOException("Append requires text content.");
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.AppendAllTextAsync(resolvedPath, content, cancellationToken);
        return new LocalWorkspaceResult($"appended {content.Length} characters", new Dictionary<string, object?> { ["character_count"] = content.Length });
    }

    public async Task<LocalWorkspaceResult> WriteAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new IOException("Write requires text content.");
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(resolvedPath, content, cancellationToken);
        return new LocalWorkspaceResult($"wrote {content.Length} characters", new Dictionary<string, object?> { ["character_count"] = content.Length });
    }

    public Task<LocalWorkspaceResult> DeleteAsync(string resolvedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(resolvedPath))
        {
            File.Delete(resolvedPath);
            return Task.FromResult(new LocalWorkspaceResult("deleted file", new Dictionary<string, object?> { ["deleted_kind"] = "file" }));
        }

        if (Directory.Exists(resolvedPath))
        {
            Directory.Delete(resolvedPath, recursive: true);
            return Task.FromResult(new LocalWorkspaceResult("deleted directory", new Dictionary<string, object?> { ["deleted_kind"] = "directory" }));
        }

        throw new FileNotFoundException($"Delete target not found: {resolvedPath}", resolvedPath);
    }

    private async Task SearchFileAsync(string file, string pattern, List<string> matches, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(file, cancellationToken);
        var displayPath = Path.GetRelativePath(_paths.RootPath, file);

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{displayPath}:{i + 1}: {lines[i]}");
            }
        }
    }
}
