using System.Text;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.LocalWorkspace;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Clients.LocalWorkspace;

public sealed class LocalWorkspaceClient : IWorkspaceToolExecutor
{
    // TODO: revisit what an appropriate figures should actually be.
    private const int MaxListEntries = 500;
    private const int MaxReadCharacters = 120_000;
    private const int MaxSearchFiles = 500;
    private const int MaxSearchMatches = 200;
    private const int MaxMatchLineCharacters = 500;
    private const long MaxSearchFileBytes = 1_048_576;
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

        var entries = new SortedSet<ListEntry>(ListEntryComparer.Instance);
        var entryCount = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(resolvedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            entries.Add(new ListEntry(path, Path.GetFileName(path), Directory.Exists(path)));
            if (entries.Count > MaxListEntries)
            {
                entries.Remove(entries.Max!);
            }
        }

        var rendered = entries.Select(entry => entry.IsDirectory ? entry.Name + Path.DirectorySeparatorChar : entry.Name).ToList();
        var text = rendered.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, rendered);
        var truncated = entryCount > MaxListEntries;
        if (truncated)
        {
            text += Environment.NewLine + $"[truncated to the first {MaxListEntries} of {entryCount} entries]";
        }

        return Task.FromResult(new LocalWorkspaceResult(text, new Dictionary<string, object?>
        {
            ["entry_count"] = entryCount,
            ["returned_entry_count"] = rendered.Count,
            ["truncated"] = truncated
        }));
    }

    public async Task<LocalWorkspaceResult> ReadAsync(string resolvedPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {resolvedPath}", resolvedPath);
        }

        var (text, truncated) = await ReadTextPrefixAsync(resolvedPath, cancellationToken);
        var fileSize = new FileInfo(resolvedPath).Length;
        var output = truncated ? text + Environment.NewLine + $"[truncated after {MaxReadCharacters} characters]" : text;
        return new LocalWorkspaceResult(output, new Dictionary<string, object?> { ["character_count"] = text.Length, ["file_size_bytes"] = fileSize, ["truncated"] = truncated });
    }

    public async Task<LocalWorkspaceResult> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new IOException("Search requires a non-empty pattern.");
        }

        var matches = new List<string>();
        var state = new SearchState();

        if (File.Exists(resolvedPath))
        {
            await SearchFileAsync(resolvedPath, pattern, matches, state, cancellationToken);
        }
        else if (Directory.Exists(resolvedPath))
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };

            var searchFiles = new List<string>(MaxSearchFiles);
            foreach (var file in Directory.EnumerateFiles(resolvedPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (searchFiles.Count >= MaxSearchFiles)
                {
                    state.Truncated = true;
                    break;
                }

                searchFiles.Add(file);
            }

            foreach (var file in searchFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                await SearchFileAsync(file, pattern, matches, state, cancellationToken);
                if (matches.Count >= MaxSearchMatches)
                {
                    break;
                }
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Search target not found: {resolvedPath}");
        }

        var text = matches.Count == 0 ? "(no matches)" : string.Join(Environment.NewLine, matches);
        if (state.Truncated)
        {
            text += Environment.NewLine + $"[truncated after {state.FilesScanned} files and {matches.Count} matches]";
        }

        return new LocalWorkspaceResult(text, new Dictionary<string, object?>
        {
            ["match_count"] = matches.Count,
            ["pattern_length"] = pattern.Length,
            ["files_scanned"] = state.FilesScanned,
            ["skipped_large_files"] = state.SkippedLargeFiles,
            ["truncated"] = state.Truncated
        });
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

    private static async Task<(string Text, bool Truncated)> ReadTextPrefixAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxReadCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var take = Math.Min(count, MaxReadCharacters);
        var text = new string(buffer, 0, take);
        if (text.Contains('\0'))
        {
            throw new IOException("File appears to be binary or contains null bytes.");
        }

        return (text, count > MaxReadCharacters || stream.Position < stream.Length);
    }

    private async Task SearchFileAsync(string file, string pattern, List<string> matches, SearchState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.FilesScanned++;
        var fileSize = new FileInfo(file).Length;
        if (fileSize > MaxSearchFileBytes)
        {
            state.SkippedLargeFiles++;
            state.Truncated = true;
            return;
        }

        var displayPath = Path.GetRelativePath(_paths.RootPath, file);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken) ?? "";
            lineNumber++;
            if (line.Contains('\0'))
            {
                state.Truncated = true;
                return;
            }

            if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add($"{displayPath}:{lineNumber}: {FormatMatchLine(line)}");
            if (matches.Count >= MaxSearchMatches)
            {
                state.Truncated = true;
                return;
            }
        }
    }

    private static string FormatMatchLine(string line)
    {
        return line.Length <= MaxMatchLineCharacters
            ? line
            : line[..MaxMatchLineCharacters] + " [line truncated]";
    }

    private sealed class SearchState
    {
        public int FilesScanned { get; set; }

        public int SkippedLargeFiles { get; set; }

        public bool Truncated { get; set; }
    }

    private sealed record ListEntry(string Path, string Name, bool IsDirectory);

    private sealed class ListEntryComparer : IComparer<ListEntry>
    {
        public static ListEntryComparer Instance { get; } = new();

        public int Compare(ListEntry? left, ListEntry? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var kind = right.IsDirectory.CompareTo(left.IsDirectory);
            if (kind != 0)
            {
                return kind;
            }

            var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (name != 0)
            {
                return name;
            }

            name = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return name != 0 ? name : StringComparer.Ordinal.Compare(left.Path, right.Path);
        }
    }
}
