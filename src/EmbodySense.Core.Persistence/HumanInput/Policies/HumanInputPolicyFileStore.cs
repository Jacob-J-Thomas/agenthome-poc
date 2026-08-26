using System.Collections.Concurrent;
using System.Text;
using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

/// <summary>Persists and resolves bounded exact immutable Human Input policy revisions from one workspace-owned file source.</summary>
/// <remarks>Every lookup names both policy and revision. The store never follows replacement records, selects a latest revision, or provides a timeout default.</remarks>
public sealed class HumanInputPolicyFileStore : IHumanInputPolicySource
{
    private const int MaximumLockAttempts = 20;
    private static readonly TimeSpan _lockRetryDelay = TimeSpan.FromMilliseconds(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly string _rootPath;
    private readonly string _generationPath;
    private readonly string _lockPath;
    private readonly HumanInputPolicyFileStoreOptions _options;

    /// <summary>Creates the canonical bounded Human Input policy source for one initialized workspace.</summary>
    /// <param name="paths">The workspace whose private <c>.agent</c> policy source will be used.</param>
    /// <param name="options">Optional finite artifact-count and byte limits.</param>
    public HumanInputPolicyFileStore(WorkspacePaths paths, HumanInputPolicyFileStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _options = Validate(options ?? new HumanInputPolicyFileStoreOptions());
        _rootPath = Path.Combine(paths.AgentPath, "human-input", "policies");
        _generationPath = Path.Combine(_rootPath, "generation");
        _lockPath = Path.Combine(_rootPath, "mutation.lock");
    }

    /// <inheritdoc />
    public async Task<HumanInputPolicySourceReadResult> ReadAsync(HumanInputPolicyReference reference, CancellationToken cancellationToken = default)
    {
        if (!IsReference(reference)) return Read(HumanInputPolicySourceReadStatus.Unavailable, null, 0);
        try
        {
            await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
            var generation = await ReadGenerationAsync(cancellationToken).ConfigureAwait(false);
            var path = PathFor(reference);
            if (!File.Exists(path)) return Read(HumanInputPolicySourceReadStatus.NotFound, null, generation);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 1 || bytes.Length > _options.MaximumArtifactUtf8Bytes) return Read(HumanInputPolicySourceReadStatus.Unavailable, null, generation);
            var policy = HumanInputPolicyArtifactJson.Deserialize(bytes);
            return Equals(policy.Reference, reference) ? Read(HumanInputPolicySourceReadStatus.Ready, policy, generation) : Read(HumanInputPolicySourceReadStatus.Unavailable, null, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Read(HumanInputPolicySourceReadStatus.Unavailable, null, 0);
        }
    }

    /// <summary>Appends one exact immutable policy revision when the caller's source generation remains current.</summary>
    /// <param name="artifact">The complete hash-authenticated policy artifact.</param>
    /// <param name="expectedStoreGeneration">The exact source generation observed before writing.</param>
    /// <param name="cancellationToken">A token that cancels before an unproven durable write boundary.</param>
    /// <returns>A committed, exact-replay, conflict, invalid, or unavailable result.</returns>
    public async Task<HumanInputPolicyFileStoreWriteResult> CommitAsync(HumanInputPolicyArtifact artifact, long expectedStoreGeneration, CancellationToken cancellationToken = default)
    {
        if (expectedStoreGeneration < 0 || !HumanInputPolicyArtifactValidator.Validate(artifact).IsValid) return Write(HumanInputPolicyFileStoreWriteStatus.Invalid, 0);
        try
        {
            await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
            var generation = await ReadGenerationAsync(cancellationToken).ConfigureAwait(false);
            var path = PathFor(artifact.Reference);
            if (File.Exists(path))
            {
                var existing = HumanInputPolicyArtifactJson.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                return Equals(existing, artifact) ? Write(HumanInputPolicyFileStoreWriteStatus.Replayed, generation) : Write(HumanInputPolicyFileStoreWriteStatus.Invalid, generation);
            }
            if (generation != expectedStoreGeneration) return Write(HumanInputPolicyFileStoreWriteStatus.Conflict, generation);
            if (generation == long.MaxValue) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, generation);
            Directory.CreateDirectory(_rootPath);
            if (PolicyArtifactCount() >= _options.MaximumArtifacts) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, generation);
            var bytes = HumanInputPolicyArtifactJson.Serialize(artifact);
            if (bytes.Length > _options.MaximumArtifactUtf8Bytes) return Write(HumanInputPolicyFileStoreWriteStatus.Invalid, generation);
            await WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            var next = generation + 1;
            await WriteAtomicallyAsync(_generationPath, Encoding.ASCII.GetBytes(next.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
            return Write(HumanInputPolicyFileStoreWriteStatus.Committed, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
        }
    }

    private async Task<HumanInputPolicyFileStoreLease> EnterAsync(CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(_rootPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootPath);
            for (var attempt = 0; attempt < MaximumLockAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    if (CustomLoopCrossProcessFileLock.TryAcquire(stream)) return new HumanInputPolicyFileStoreLease(stream, gate);
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException) when (attempt + 1 < MaximumLockAttempts)
                {
                    // A separate process owns the exact source mutation boundary.
                }

                await Task.Delay(_lockRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            throw new IOException("The bounded Human Input policy source mutation lease is unavailable.");
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<long> ReadGenerationAsync(CancellationToken cancellationToken)
    {
        var artifactCount = PolicyArtifactCount();
        if (!File.Exists(_generationPath))
        {
            if (artifactCount != 0) throw new FormatException("The Human Input policy generation is missing.");
            return 0;
        }

        var bytes = await File.ReadAllBytesAsync(_generationPath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length is < 1 or > 20 || !long.TryParse(Encoding.ASCII.GetString(bytes), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation) || generation < 0) throw new FormatException("The Human Input policy generation is invalid.");
        if (generation != artifactCount) throw new FormatException("The Human Input policy generation does not match the immutable artifact catalog.");
        return generation;
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bytes.Length, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PathFor(HumanInputPolicyReference reference) => Path.Combine(_rootPath, reference.PolicyId + "@" + reference.RevisionId + ".json");

    private int PolicyArtifactCount()
    {
        var count = Directory.EnumerateFiles(_rootPath, "*.json", SearchOption.TopDirectoryOnly).Take(_options.MaximumArtifacts + 1).Count();
        if (count > _options.MaximumArtifacts) throw new FormatException("The Human Input policy source exceeds its immutable artifact bound.");
        return count;
    }

    private static bool IsReference(HumanInputPolicyReference? reference) => reference is not null && HumanInputPolicyReference.TryParse(reference.ToString(), out _);

    private static HumanInputPolicySourceReadResult Read(HumanInputPolicySourceReadStatus status, HumanInputPolicyArtifact? policy, long generation) => new(status, policy, generation);

    private static HumanInputPolicyFileStoreWriteResult Write(HumanInputPolicyFileStoreWriteStatus status, long generation) => new(status, generation);

    private static HumanInputPolicyFileStoreOptions Validate(HumanInputPolicyFileStoreOptions options)
    {
        if (options.MaximumArtifacts is < 1 or > 1_024 || options.MaximumArtifactUtf8Bytes is < 256 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options));
        return options;
    }

}
