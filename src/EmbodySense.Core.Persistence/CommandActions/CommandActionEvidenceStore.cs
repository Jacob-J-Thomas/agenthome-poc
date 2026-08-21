using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.CommandActions;

/// <summary>Persists bounded immutable command preparation and redacted outcome evidence beneath one workspace.</summary>
public sealed class CommandActionEvidenceStore : ICommandActionEvidenceStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };
    private readonly string _outcomeRoot;
    private readonly string _preparationRoot;
    private readonly CustomLoopArtifactPathGuard _guard;

    /// <summary>Creates one workspace-scoped command evidence store.</summary>
    public CommandActionEvidenceStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = Path.Combine(paths.AgentPath, "loops", "execution", "command-actions");
        _preparationRoot = Path.Combine(root, "preparations");
        _outcomeRoot = Path.Combine(root, "outcomes");
        _guard = new CustomLoopArtifactPathGuard(paths.RootPath);
    }

    /// <inheritdoc />
    public Task RetainPreparationAsync(CommandActionPreparationEvidence evidence, CancellationToken cancellationToken = default)
        => RetainAsync(_preparationRoot, evidence?.EvidenceId, evidence, CommandActionEvidenceContract.ValidatePreparation, cancellationToken);

    /// <inheritdoc />
    public Task<CommandActionPreparationEvidence?> ReadPreparationAsync(string evidenceId, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(evidenceId, "command-before-")
            ? ReadAsync<CommandActionPreparationEvidence>(_preparationRoot, evidenceId, CommandActionEvidenceContract.ValidatePreparation, cancellationToken)
            : Task.FromResult<CommandActionPreparationEvidence?>(null);

    /// <inheritdoc />
    public async Task RetainOutcomeAsync(CommandActionOutcomeEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (CommandActionEvidenceContract.ValidateOutcome(evidence) is { } reasonCode)
        {
            throw new ArgumentException(reasonCode, nameof(evidence));
        }
        var encoded = EncodeBounded(evidence);
        _guard.PrepareRoot(_outcomeRoot);
        using var ownership = _guard.AcquireExclusiveMutationLock(_outcomeRoot);
        var paths = EvidencePaths(_outcomeRoot);
        var path = _guard.GetFilePath(_outcomeRoot, evidence.EvidenceId + ".json");
        foreach (var candidatePath in paths)
        {
            var candidate = await ReadPathAsync<CommandActionOutcomeEvidence>(
                _outcomeRoot,
                candidatePath,
                CommandActionEvidenceContract.ValidateOutcome,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.IdempotencyOperationId, evidence.IdempotencyOperationId, StringComparison.Ordinal)
                && candidate.EffectGeneration == evidence.EffectGeneration
                && !string.Equals(candidate.EvidenceId, evidence.EvidenceId, StringComparison.Ordinal))
            {
                throw new FormatException("Immutable command outcome evidence conflicts for one operation generation.");
            }
        }
        await WriteOrVerifyAsync<CommandActionOutcomeEvidence>(_outcomeRoot, path, encoded, CommandActionEvidenceContract.ValidateOutcome, paths, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CommandActionOutcomeEvidence?> ReadOutcomeAsync(string evidenceId, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(evidenceId, "command-outcome-")
            ? ReadAsync<CommandActionOutcomeEvidence>(_outcomeRoot, evidenceId, CommandActionEvidenceContract.ValidateOutcome, cancellationToken)
            : Task.FromResult<CommandActionOutcomeEvidence?>(null);

    /// <inheritdoc />
    public async Task<CommandActionOutcomeEvidence?> ReadOutcomeByOperationAsync(
        string idempotencyOperationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!CommandActionFingerprint.IsEvidenceIdentifier(idempotencyOperationId) || effectGeneration < 1)
        {
            return null;
        }
        _guard.PrepareRoot(_outcomeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_outcomeRoot, cancellationToken).ConfigureAwait(false);
        CommandActionOutcomeEvidence? match = null;
        foreach (var path in EvidencePaths(_outcomeRoot))
        {
            var candidate = await ReadPathAsync<CommandActionOutcomeEvidence>(
                _outcomeRoot,
                path,
                CommandActionEvidenceContract.ValidateOutcome,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal)
                && candidate.EffectGeneration == effectGeneration)
            {
                if (match is not null)
                {
                    throw new FormatException("Command outcome evidence contains conflicting records for one operation generation.");
                }
                match = candidate;
            }
        }
        return match;
    }

    private async Task RetainAsync<T>(
        string root,
        string? identifier,
        T? evidence,
        Func<T?, string?> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var reasonCode = validate(evidence);
        if (!CommandActionFingerprint.IsEvidenceIdentifier(identifier) || reasonCode is not null)
        {
            throw new ArgumentException(reasonCode ?? "Command evidence identifier is invalid.", nameof(evidence));
        }
        var encoded = EncodeBounded(evidence);
        _guard.PrepareRoot(root);
        using var ownership = _guard.AcquireExclusiveMutationLock(root);
        var paths = EvidencePaths(root);
        var path = _guard.GetFilePath(root, identifier + ".json");
        await WriteOrVerifyAsync(root, path, encoded, validate, paths, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteOrVerifyAsync<T>(
        string root,
        string path,
        string encoded,
        Func<T?, string?> validate,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!paths.Contains(path, StringComparer.Ordinal)
            && paths.Count >= CommandActionContractLimits.MaxEvidenceRecordsPerKind)
        {
            throw new InvalidOperationException("Command evidence capacity is exhausted.");
        }
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(root, path, encoded, cancellationToken).ConfigureAwait(false))
        {
            var retained = await ReadPathAsync<T>(root, path, validate, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(JsonSerializer.Serialize(retained, _jsonOptions), encoded, StringComparison.Ordinal))
            {
                throw new FormatException("Immutable command evidence conflicts with retained content-addressed evidence.");
            }
        }
    }

    private async Task<T?> ReadAsync<T>(
        string root,
        string identifier,
        Func<T?, string?> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!CommandActionFingerprint.IsEvidenceIdentifier(identifier))
        {
            return null;
        }
        _guard.PrepareRoot(root);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(root, cancellationToken).ConfigureAwait(false);
        var path = _guard.GetFilePath(root, identifier + ".json");
        return File.Exists(path)
            ? await ReadPathAsync(root, path, validate, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<T> ReadPathAsync<T>(
        string root,
        string path,
        Func<T?, string?> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await _guard.ReadAllBytesAsync(
            root,
            path,
            CommandActionContractLimits.MaxEvidenceUtf8Bytes,
            "Command action evidence",
            cancellationToken).ConfigureAwait(false);
        T? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Command action evidence is malformed.", exception);
        }
        if (validate(evidence) is { } reasonCode)
        {
            throw new FormatException($"Command action evidence is not authentic: {reasonCode}.");
        }
        return evidence!;
    }

    private IReadOnlyList<string> EvidencePaths(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Take(CommandActionContractLimits.MaxEvidenceRecordsPerKind + 2)
            .ToArray();
        if (entries.Length > CommandActionContractLimits.MaxEvidenceRecordsPerKind + 1)
        {
            throw new FormatException("Command action evidence exceeds its finite record bound.");
        }
        var paths = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                continue;
            }
            if (Directory.Exists(entry)
                || !name.EndsWith(".json", StringComparison.Ordinal)
                || !CommandActionFingerprint.IsEvidenceIdentifier(name[..^5]))
            {
                throw new FormatException("Command action evidence contains an unsupported artifact.");
            }
            paths.Add(entry);
        }
        return paths;
    }

    private static string EncodeBounded<T>(T evidence)
    {
        var encoded = JsonSerializer.Serialize(evidence, _jsonOptions);
        if (Encoding.UTF8.GetByteCount(encoded) > CommandActionContractLimits.MaxEvidenceUtf8Bytes)
        {
            throw new InvalidOperationException("Command action evidence exceeds its immutable record byte bound.");
        }
        return encoded;
    }

    private static bool IsContentAddressedIdentifier(string? value, string prefix)
        => value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && CommandActionFingerprint.IsCanonicalSha256(value[prefix.Length..]);
}
