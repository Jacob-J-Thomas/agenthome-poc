using EmbodySense.Core.Common.Loops;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ToolResults.Models;

namespace EmbodySense.Core.Persistence.ToolResults;

/// <summary>
/// Retains governed tool results as bounded version-1 manifests and content-hashed UTF-8 chunks.
/// </summary>
/// <remarks>
/// Retention validates existing manifests and chunk hashes before reuse, stages a complete artifact directory before rename,
/// and evicts oldest validated artifacts under the workspace lock to maintain count and byte quotas. Stamps are only an
/// accounting optimization and never replace integrity validation before reuse or deletion. Corrupt, unknown, unsafe, or
/// unsupported artifacts fail closed; no schema migration or legacy reader is provided.
/// </remarks>
public sealed class ToolResultRetentionStore : IToolResultRetentionStore
{
    private const int CurrentSchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string StagingPrefix = ".staging-";
    private const string EvictionStagingPrefix = ".evicting-";
    private const string RetentionPolicy = "oldest-first within 256 artifacts and 64 MiB; full response chunks are sensitive local workspace evidence";
    private static readonly TimeSpan _lockRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan _maxLockWait = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private readonly WorkspacePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, AccountedArtifactSnapshot> _accountedArtifacts = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolResultRetentionStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="timeProvider">The time provider.</param>
    public ToolResultRetentionStore(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Retains the bounded response content and returns the manifest reference admitted to the transcript.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the tool result retention reference.</returns>
    public async Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(loopDefinition);

        try
        {
            ValidateResult(result);
            PrepareRetentionRoot();
            await using var lease = await AcquireLeaseAsync(cancellationToken);
            CleanupStagingDirectories();
            var existingArtifacts = await LoadArtifactsAsync(cancellationToken);
            var recoveredEvictions = await EvictToLimitsAsync(existingArtifacts, cancellationToken);
            var retainedAtUtc = NextRetainedAtUtc(existingArtifacts);
            var prepared = PrepareArtifact(result, loopDefinition, retainedAtUtc);
            var existing = existingArtifacts.SingleOrDefault(artifact => !artifact.Evicted && string.Equals(artifact.Manifest.RequestId, result.RequestId, StringComparison.Ordinal));
            if (existing is not null)
            {
                var revalidated = await RevalidateArtifactAsync(existing, cancellationToken);
                return SameArtifact(revalidated.Manifest, prepared.Manifest)
                    ? CreateReference(revalidated.Manifest, recoveredEvictions)
                    : Unavailable(result, "The broker request id is already bound to different retained response evidence.");
            }

            if (prepared.ManifestBytes.LongLength > ToolResultRetentionLimits.MaxManifestUtf8Bytes
                || prepared.TotalUtf8Bytes > ToolResultRetentionLimits.MaxArtifactUtf8Bytes)
            {
                return Unavailable(result, "The complete governed response exceeds its bounded manifest or artifact byte limit.");
            }

            if (IsToolResponseInspection(result)
                && WouldExceedWorkspaceLimits(existingArtifacts, prepared.TotalUtf8Bytes))
            {
                return Unavailable(result, "The inspection response was not self-retained because doing so would evict tool-response evidence being inspected.");
            }

            await ValidateRequiredEvictionsAsync(existingArtifacts, prepared.TotalUtf8Bytes, cancellationToken);
            await WriteArtifactAsync(prepared, cancellationToken);
            var retainedArtifact = new RetainedArtifact(Path.Combine(_paths.ToolResponsesPath, prepared.Manifest.RequestId), prepared.Manifest, prepared.TotalUtf8Bytes, contentValidated: true);
            _accountedArtifacts[prepared.Manifest.RequestId] = CaptureAccountedArtifact(retainedArtifact.Directory, prepared.Manifest, prepared.TotalUtf8Bytes);
            existingArtifacts.Add(retainedArtifact);
            var evicted = recoveredEvictions + await EvictToLimitsAsync(existingArtifacts, cancellationToken);
            if (retainedArtifact.Evicted || !Directory.Exists(retainedArtifact.Directory))
            {
                return Unavailable(result, "The newly written full-response artifact did not survive bounded quota enforcement.");
            }

            return CreateReference(prepared.Manifest, evicted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(result, $"Durable full-response retention failed closed with {exception.GetType().Name}; no partial response artifact is advertised.");
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_paths.ToolResponseRetentionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException exception) when (IsLockContention(exception) && wait.Elapsed < _maxLockWait)
            {
                await Task.Delay(_lockRetryDelay, cancellationToken);
            }
        }
    }

    private void PrepareRetentionRoot()
    {
        if (!Directory.Exists(_paths.RootPath))
        {
            throw new InvalidDataException("The tool-response retention workspace root does not exist.");
        }

        EnsurePlainDirectory(_paths.RootPath);
        foreach (var directory in new[] { _paths.AgentPath, _paths.LogsPath, _paths.ToolResponsesPath })
        {
            EnsurePlainDirectory(Path.GetDirectoryName(directory)!);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EnsurePlainDirectory(directory);
        }

        if (File.Exists(_paths.ToolResponseRetentionLockPath) && File.GetAttributes(_paths.ToolResponseRetentionLockPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The tool-response retention lock cannot be a reparse point.");
        }
    }

    private void CleanupStagingDirectories()
    {
        foreach (var prefix in new[] { StagingPrefix, EvictionStagingPrefix })
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.ToolResponsesPath, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                var stagingId = Path.GetFileName(directory)[prefix.Length..];
                if (!IsRequestId(stagingId))
                {
                    throw new InvalidDataException("The tool-response retention root contains an unrecognized staging directory.");
                }

                EnsurePlainDirectory(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private async Task<List<RetainedArtifact>> LoadArtifactsAsync(CancellationToken cancellationToken)
    {
        var allowedRootFile = Path.GetFullPath(_paths.ToolResponseRetentionLockPath);
        foreach (var file in Directory.EnumerateFiles(_paths.ToolResponsesPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!PathEquals(file, allowedRootFile))
            {
                throw new InvalidDataException("The tool-response retention root contains an unrecognized file.");
            }
        }

        var artifacts = new List<RetainedArtifact>();
        var presentRequestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(_paths.ToolResponsesPath, "*", SearchOption.TopDirectoryOnly))
        {
            EnsurePlainDirectory(directory);
            var requestId = Path.GetFileName(directory);
            if (!IsRequestId(requestId))
            {
                throw new InvalidDataException("The tool-response retention root contains an unrecognized directory.");
            }

            presentRequestIds.Add(requestId);
            if (_accountedArtifacts.TryGetValue(requestId, out var cached)
                && MatchesAccountedArtifact(directory, cached))
            {
                // Stable stamps are an accounting optimization only. Any operation that re-advertises or evicts
                // this evidence performs full manifest and chunk hash validation first.
                artifacts.Add(new RetainedArtifact(directory, cached.Manifest, cached.TotalUtf8Bytes, contentValidated: false));
                continue;
            }

            var manifestPath = Path.Combine(directory, ManifestFileName);
            var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
            var totalUtf8Bytes = await ValidateArtifactAsync(directory, requestId, manifest, cancellationToken);
            _accountedArtifacts[requestId] = CaptureAccountedArtifact(directory, manifest, totalUtf8Bytes);
            artifacts.Add(new RetainedArtifact(directory, manifest, totalUtf8Bytes, contentValidated: true));
        }

        foreach (var staleRequestId in _accountedArtifacts.Keys.Where(requestId => !presentRequestIds.Contains(requestId)).ToArray())
        {
            _accountedArtifacts.Remove(staleRequestId);
        }

        return artifacts;
    }

    private async Task<RetainedArtifact> RevalidateArtifactAsync(RetainedArtifact artifact, CancellationToken cancellationToken)
    {
        var requestId = artifact.Manifest.RequestId;
        var manifest = await ReadManifestAsync(Path.Combine(artifact.Directory, ManifestFileName), cancellationToken);
        var totalUtf8Bytes = await ValidateArtifactAsync(artifact.Directory, requestId, manifest, cancellationToken);
        _accountedArtifacts[requestId] = CaptureAccountedArtifact(artifact.Directory, manifest, totalUtf8Bytes);
        artifact.ContentValidated = true;
        return new RetainedArtifact(artifact.Directory, manifest, totalUtf8Bytes, contentValidated: true);
    }

    private static async Task<ToolResultArtifactManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var file = new FileInfo(manifestPath);
        if (!file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Length < 1
            || file.Length > ToolResultRetentionLimits.MaxManifestUtf8Bytes)
        {
            throw new InvalidDataException("A retained tool-response manifest is missing, redirected, empty, or oversized.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > ToolResultRetentionLimits.MaxManifestUtf8Bytes)
        {
            throw new InvalidDataException("A retained tool-response manifest changed outside its governed size bound.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<ToolResultArtifactManifest>(bytes, _jsonOptions)
            ?? throw new InvalidDataException("A retained tool-response manifest is empty.");
    }

    private static async Task<long> ValidateArtifactAsync(string directory, string requestId, ToolResultArtifactManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion || !string.Equals(manifest.RequestId, requestId, StringComparison.Ordinal) || !IsSha256(manifest.ContentSha256))
        {
            throw new InvalidDataException("A retained tool-response manifest has an incompatible identity or schema.");
        }

        var maximumChunkCount = ((ToolResultRetentionLimits.MaxOutputCharacters + ToolResultRetentionLimits.MaxChunkCharacters - 1) / ToolResultRetentionLimits.MaxChunkCharacters) + 1;
        if (manifest.CharacterCount < 0
            || manifest.CharacterCount > ToolResultRetentionLimits.MaxOutputCharacters
            || manifest.Utf8ByteCount < 0
            || manifest.Utf8ByteCount > ToolResultRetentionLimits.MaxArtifactUtf8Bytes
            || manifest.Chunks.Length < 1
            || manifest.Chunks.Length > maximumChunkCount)
        {
            throw new InvalidDataException("A retained tool-response manifest exceeds its governed content bounds.");
        }

        var expectedFiles = new HashSet<string>(StringComparer.Ordinal) { ManifestFileName };
        var manifestFile = new FileInfo(Path.Combine(directory, ManifestFileName));
        if (!manifestFile.Exists || manifestFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("A retained tool-response manifest is missing or is a reparse point.");
        }

        long totalUtf8Bytes = manifestFile.Length;
        long chunkUtf8Bytes = 0;
        var chunkCharacters = 0;
        using var aggregateHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < manifest.Chunks.Length; index++)
        {
            var chunk = manifest.Chunks[index];
            var expectedPath = ChunkFileName(index + 1);
            if (chunk.Sequence != index + 1 || !string.Equals(chunk.Path, expectedPath, StringComparison.Ordinal) || !IsSha256(chunk.ContentSha256))
            {
                throw new InvalidDataException("A retained tool-response chunk manifest is not canonical.");
            }

            var chunkPath = Path.Combine(directory, expectedPath);
            var file = new FileInfo(chunkPath);
            if (!file.Exists
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || file.Length != chunk.Utf8ByteCount
                || file.Length > ToolResultRetentionLimits.MaxArtifactUtf8Bytes
                || chunk.CharacterCount < 0
                || chunk.CharacterCount > ToolResultRetentionLimits.MaxChunkCharacters)
            {
                throw new InvalidDataException("A retained tool-response chunk does not match its manifest bounds.");
            }

            var bytes = await File.ReadAllBytesAsync(chunkPath, cancellationToken);
            if (!string.Equals(Sha256(bytes), chunk.ContentSha256, StringComparison.Ordinal)
                || _strictUtf8.GetString(bytes).Length != chunk.CharacterCount)
            {
                throw new InvalidDataException("A retained tool-response chunk does not match its exact content hash or character count.");
            }

            aggregateHash.AppendData(bytes);
            expectedFiles.Add(expectedPath);
            totalUtf8Bytes += file.Length;
            chunkUtf8Bytes += chunk.Utf8ByteCount;
            chunkCharacters += chunk.CharacterCount;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!expectedFiles.Contains(Path.GetFileName(file)))
            {
                throw new InvalidDataException("A retained tool-response artifact contains an unrecognized file.");
            }
        }

        var aggregateContentSha256 = Convert.ToHexString(aggregateHash.GetHashAndReset()).ToLowerInvariant();
        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any()
            || chunkUtf8Bytes != manifest.Utf8ByteCount
            || chunkCharacters != manifest.CharacterCount
            || !string.Equals(aggregateContentSha256, manifest.ContentSha256, StringComparison.Ordinal)
            || totalUtf8Bytes > ToolResultRetentionLimits.MaxArtifactUtf8Bytes)
        {
            throw new InvalidDataException("A retained tool-response artifact does not match its manifest totals.");
        }

        return totalUtf8Bytes;
    }

    private DateTimeOffset NextRetainedAtUtc(IReadOnlyList<RetainedArtifact> existingArtifacts)
    {
        var now = _timeProvider.GetUtcNow();
        var latest = existingArtifacts.Where(artifact => !artifact.Evicted).Select(artifact => artifact.Manifest.RetainedAtUtc).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (latest < now)
        {
            return now;
        }

        if (latest == DateTimeOffset.MaxValue)
        {
            throw new InvalidDataException("Retained tool-response timestamps cannot advance monotonically.");
        }

        return latest.AddTicks(1);
    }

    private PreparedArtifact PrepareArtifact(ToolResult result, LoopDefinition loopDefinition, DateTimeOffset retainedAtUtc)
    {
        var chunks = new List<PreparedChunk>();
        var offset = 0;
        var sequence = 1;
        while (offset < result.OutputText.Length || chunks.Count == 0)
        {
            var characterCount = Math.Min(ToolResultRetentionLimits.MaxChunkCharacters, result.OutputText.Length - offset);
            if (characterCount > 0 && offset + characterCount < result.OutputText.Length && char.IsHighSurrogate(result.OutputText[offset + characterCount - 1]) && char.IsLowSurrogate(result.OutputText[offset + characterCount]))
            {
                characterCount--;
            }

            var content = result.OutputText.Substring(offset, characterCount);
            var bytes = _strictUtf8.GetBytes(content);
            var path = ChunkFileName(sequence);
            chunks.Add(new PreparedChunk(path, bytes, new ToolResultArtifactChunk(sequence, path, Sha256(bytes), content.Length, bytes.LongLength)));
            offset += characterCount;
            sequence++;
        }

        var correlation = result.Request.AuditCorrelation;
        var contentBytes = _strictUtf8.GetBytes(result.OutputText);
        var manifest = new ToolResultArtifactManifest(
            CurrentSchemaVersion,
            result.RequestId,
            result.Request.CorrelationId,
            correlation?.LoopId ?? loopDefinition.Id,
            correlation?.RoleId ?? loopDefinition.RoleId,
            correlation?.RunId,
            correlation?.DefinitionVersion,
            correlation?.DefinitionHash,
            correlation?.Iteration,
            correlation?.StepId,
            correlation?.Attempt,
            correlation?.AttemptCorrelationId,
            result.Request.Command,
            result.Request.TargetPath,
            result.ResolvedPath,
            result.Outcome,
            Sha256(contentBytes),
            result.OutputText.Length,
            contentBytes.LongLength,
            retainedAtUtc,
            RetentionPolicy,
            chunks.Select(chunk => chunk.Manifest).ToArray());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, _jsonOptions);
        return new PreparedArtifact(manifest, manifestBytes, chunks, manifestBytes.LongLength + chunks.Sum(chunk => chunk.Bytes.LongLength));
    }

    private async Task<int> EvictToLimitsAsync(List<RetainedArtifact> artifacts, CancellationToken cancellationToken)
    {
        var retainedCount = artifacts.Count(artifact => !artifact.Evicted);
        var retainedBytes = artifacts.Where(artifact => !artifact.Evicted).Sum(artifact => artifact.TotalUtf8Bytes);
        var evictedCount = 0;
        foreach (var artifact in artifacts.OrderBy(artifact => artifact.Manifest.RetainedAtUtc).ThenBy(artifact => artifact.Manifest.RequestId, StringComparer.Ordinal))
        {
            if (retainedCount <= ToolResultRetentionLimits.MaxArtifactsPerWorkspace && retainedBytes <= ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes)
            {
                break;
            }

            if (artifact.Evicted)
            {
                continue;
            }

            if (!artifact.ContentValidated)
            {
                await RevalidateArtifactAsync(artifact, cancellationToken);
            }

            var evictionPath = Path.Combine(_paths.ToolResponsesPath, EvictionStagingPrefix + artifact.Manifest.RequestId);
            if (Directory.Exists(evictionPath))
            {
                throw new InvalidDataException("A retained tool-response eviction staging path already exists.");
            }

            Directory.Move(artifact.Directory, evictionPath);
            _accountedArtifacts.Remove(artifact.Manifest.RequestId);
            artifact.Evicted = true;
            retainedCount--;
            retainedBytes -= artifact.TotalUtf8Bytes;
            evictedCount++;
            Directory.Delete(evictionPath, recursive: true);
        }

        if (retainedCount > ToolResultRetentionLimits.MaxArtifactsPerWorkspace || retainedBytes > ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes)
        {
            throw new InvalidDataException("Retained tool-response evidence cannot be reduced to its governed workspace limits.");
        }

        return evictedCount;
    }

    private async Task ValidateRequiredEvictionsAsync(IReadOnlyList<RetainedArtifact> artifacts, long additionalUtf8Bytes, CancellationToken cancellationToken)
    {
        var retainedCount = artifacts.Count(artifact => !artifact.Evicted) + 1;
        var retainedBytes = artifacts.Where(artifact => !artifact.Evicted).Sum(artifact => artifact.TotalUtf8Bytes) + additionalUtf8Bytes;
        foreach (var artifact in artifacts.Where(artifact => !artifact.Evicted).OrderBy(artifact => artifact.Manifest.RetainedAtUtc).ThenBy(artifact => artifact.Manifest.RequestId, StringComparer.Ordinal))
        {
            if (retainedCount <= ToolResultRetentionLimits.MaxArtifactsPerWorkspace && retainedBytes <= ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes)
            {
                return;
            }

            if (!artifact.ContentValidated)
            {
                await RevalidateArtifactAsync(artifact, cancellationToken);
            }

            retainedCount--;
            retainedBytes -= artifact.TotalUtf8Bytes;
        }
    }

    private async Task WriteArtifactAsync(PreparedArtifact artifact, CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(_paths.ToolResponsesPath, StagingPrefix + Guid.NewGuid().ToString("N"));
        var finalPath = Path.Combine(_paths.ToolResponsesPath, artifact.Manifest.RequestId);
        Directory.CreateDirectory(stagingPath);
        try
        {
            foreach (var chunk in artifact.Chunks)
            {
                await File.WriteAllBytesAsync(Path.Combine(stagingPath, chunk.Path), chunk.Bytes, cancellationToken);
            }

            await File.WriteAllBytesAsync(Path.Combine(stagingPath, ManifestFileName), artifact.ManifestBytes, cancellationToken);
            Directory.Move(stagingPath, finalPath);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    private ToolResultRetentionReference CreateReference(ToolResultArtifactManifest manifest, int evictedArtifactCount)
    {
        var manifestPath = Path.GetRelativePath(_paths.RootPath, Path.Combine(_paths.ToolResponsesPath, manifest.RequestId, ManifestFileName)).Replace(Path.DirectorySeparatorChar, '/');
        var detail = evictedArtifactCount == 0
            ? $"Retained under the {RetentionPolicy}."
            : $"Retained under the {RetentionPolicy}; evicted {evictedArtifactCount} oldest artifact(s) before this write.";
        return new ToolResultRetentionReference(
            ToolResultRetentionStatus.Retained,
            manifestPath,
            manifest.ContentSha256,
            manifest.CharacterCount,
            manifest.Utf8ByteCount,
            manifest.Chunks.Length,
            manifest.RetainedAtUtc,
            evictedArtifactCount,
            detail);
    }

    private static ToolResultRetentionReference Unavailable(ToolResult result, string detail)
    {
        return new ToolResultRetentionReference(ToolResultRetentionStatus.Unavailable, null, null, result.OutputText.Length, null, null, null, 0, detail);
    }

    private static bool SameArtifact(ToolResultArtifactManifest left, ToolResultArtifactManifest right)
    {
        return string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
            && left.CharacterCount == right.CharacterCount
            && left.Utf8ByteCount == right.Utf8ByteCount
            && left.Outcome == right.Outcome
            && left.Command == right.Command
            && string.Equals(left.TargetPath, right.TargetPath, StringComparison.Ordinal)
            && string.Equals(left.ResolvedPath, right.ResolvedPath, StringComparison.Ordinal);
    }

    private static void ValidateResult(ToolResult result)
    {
        if (!IsRequestId(result.RequestId))
        {
            throw new InvalidDataException("A retained tool response requires the broker's canonical 32-character request id.");
        }

        if (result.OutputText.Length > ToolResultRetentionLimits.MaxOutputCharacters)
        {
            throw new InvalidDataException($"The complete governed response exceeds the {ToolResultRetentionLimits.MaxOutputCharacters}-character retention limit.");
        }
    }

    private bool IsToolResponseInspection(ToolResult result)
    {
        if (result.Request.Command is not (ToolCommand.List or ToolCommand.Read or ToolCommand.Search))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ToolResponsesPath));
        var candidate = Path.GetFullPath(result.ResolvedPath);
        if (PathEquals(root, candidate))
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static bool WouldExceedWorkspaceLimits(IReadOnlyList<RetainedArtifact> artifacts, long additionalUtf8Bytes)
    {
        var retained = artifacts.Where(artifact => !artifact.Evicted).ToArray();
        return retained.Length + 1 > ToolResultRetentionLimits.MaxArtifactsPerWorkspace
            || retained.Sum(artifact => artifact.TotalUtf8Bytes) + additionalUtf8Bytes > ToolResultRetentionLimits.MaxWorkspaceUtf8Bytes;
    }

    private static AccountedArtifactSnapshot CaptureAccountedArtifact(string directory, ToolResultArtifactManifest manifest, long totalUtf8Bytes)
    {
        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException("A retained tool-response artifact contains an unrecognized directory.");
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToDictionary(
                file => file.Name,
                file =>
                {
                    if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new InvalidDataException("A retained tool-response artifact file is missing or redirected.");
                    }

                    return new ArtifactFileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
                },
                StringComparer.Ordinal);
        return new AccountedArtifactSnapshot(manifest, totalUtf8Bytes, files);
    }

    private static bool MatchesAccountedArtifact(string directory, AccountedArtifactSnapshot cached)
    {
        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
        {
            return false;
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(path => new FileInfo(path)).ToArray();
        if (files.Length != cached.Files.Count)
        {
            return false;
        }

        foreach (var file in files)
        {
            if (!file.Exists
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !cached.Files.TryGetValue(file.Name, out var expected)
                || file.Length != expected.Length
                || file.LastWriteTimeUtc.Ticks != expected.LastWriteTimeUtcTicks)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Retained tool-response directories cannot be reparse points.");
        }
    }

    private static bool IsRequestId(string value) => value.Length == 32 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ChunkFileName(int sequence) => $"{sequence:D4}.txt";

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsLockContention(IOException exception)
    {
        var error = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? error is 32 or 33
            : OperatingSystem.IsMacOS()
                ? error == 35
                : error == 11;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

}
