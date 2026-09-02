using System.Text;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

/// <summary>Reads the canonical case evidence needed to prove one persisted reconciled effect successor.</summary>
/// <remarks>
/// This reader is intentionally owned by the effect-attempt store. Effect reads must remain safe when a caller has not
/// constructed the reconciliation facade, so a non-null resolution reference is never treated as proof by itself. The
/// caller holds the shared effect-attempt mutation/read lock for the entire operation.
/// </remarks>
internal sealed class GovernedLoopEffectReconciliationProofReader
{
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly long _maximumStoreBytes;
    private readonly string _root;

    internal GovernedLoopEffectReconciliationProofReader(CustomLoopArtifactPathGuard guard, string root, long maximumStoreBytes)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStoreBytes, 1);
        _guard = guard;
        _maximumStoreBytes = maximumStoreBytes;
        _root = root;
    }

    /// <summary>Reads every current canonical reconciliation case under the caller's shared persistence lock.</summary>
    internal async Task<IReadOnlyDictionary<string, GovernedLoopEffectReconciliationCase>?> ReadCurrentCasesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var casesByStorageKey = new Dictionary<string, List<(long Version, string Hash, GovernedLoopEffectReconciliationCase Value)>>(StringComparer.Ordinal);
            var versionFileCount = 0;
            long proofBytes = 0;
            foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++versionFileCount > checked(GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases * GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions))
                {
                    return null;
                }

                var fileName = Path.GetFileName(path);
                if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseVersionFile(fileName, out var storageKey, out var version, out var hash))
                {
                    return null;
                }

                var safePath = _guard.GetFilePath(_root, fileName);
                var fileLength = _guard.GetFileLength(_root, safePath);
                if (fileLength > GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes || !TryAddProofBytes(ref proofBytes, fileLength))
                {
                    return null;
                }

                var bytes = await _guard.ReadAllBytesAsync(
                    _root,
                    safePath,
                    GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes,
                    "Reconciliation case proof",
                    cancellationToken).ConfigureAwait(false);
                if (!GovernedLoopEffectReconciliationRecordCodec.TryDecode(bytes, out var value, out _)
                    || value is null
                    || value.CaseVersion != version
                    || !string.Equals(value.ContentHash, hash, StringComparison.Ordinal)
                    || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(value.CaseId), storageKey, StringComparison.Ordinal))
                {
                    return null;
                }

                if (!casesByStorageKey.TryGetValue(storageKey, out var versions))
                {
                    if (casesByStorageKey.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases)
                    {
                        return null;
                    }

                    versions = [];
                    casesByStorageKey.Add(storageKey, versions);
                }
                if (versions.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions)
                {
                    return null;
                }
                versions.Add((version, hash, value));
            }

            var caseHeads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.head", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseHeadFile(fileName, out var storageKey))
                {
                    return null;
                }
                if (caseHeads.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases)
                {
                    return null;
                }

                var headPath = _guard.GetFilePath(_root, fileName);
                var headLength = _guard.GetFileLength(_root, headPath);
                if (headLength != GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters || !TryAddProofBytes(ref proofBytes, headLength))
                {
                    return null;
                }

                var head = Encoding.ASCII.GetString(await _guard.ReadAllBytesAsync(
                    _root,
                    headPath,
                    GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters,
                    "Reconciliation case head proof",
                    cancellationToken).ConfigureAwait(false));
                if (!IsHash(head) || !caseHeads.TryAdd(storageKey, head))
                {
                    return null;
                }
            }

            foreach (var pair in casesByStorageKey)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = pair.Value.OrderBy(item => item.Version).ToArray();
                if (!caseHeads.TryGetValue(pair.Key, out var head)
                    || versions.Length == 0
                    || versions.Any(item => !string.Equals(item.Value.CaseId, versions[0].Value.CaseId, StringComparison.Ordinal))
                    || versions[0].Version != 1
                    || versions[0].Value.PreviousContentHash is not null
                    || !string.Equals(head, versions[^1].Hash, StringComparison.Ordinal))
                {
                    return null;
                }

                for (var index = 1; index < versions.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (versions[index].Version != versions[index - 1].Version + 1
                        || !GovernedLoopEffectReconciliationContractValidator.ValidateTransition(versions[index - 1].Value, versions[index].Value).IsValid)
                    {
                        return null;
                    }
                }
            }

            if (caseHeads.Count != casesByStorageKey.Count)
            {
                return null;
            }

            return casesByStorageKey.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(item => item.Version).Last().Value,
                StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>Returns whether the successor is proved by exactly one current canonical reconciliation case.</summary>
    internal bool IsCanonicalSuccessor(
        GovernedLoopEffectAttempt current,
        GovernedLoopEffectAttempt next,
        IReadOnlyDictionary<string, GovernedLoopEffectReconciliationCase> currentCases)
    {
        if (current.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired
            || next.Payload.Phase != GovernedLoopEffectPhase.Reconciled
            || GovernedLoopEffectAttemptContract.Validate(current) is not null
            || GovernedLoopEffectAttemptContract.Validate(next) is not null
            || next.Payload.ReconciliationEvidenceId is not { } resolutionId
            || !CustomLoopArtifactIdentifier.IsValid(resolutionId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters))
        {
            return false;
        }

        var matchingCases = currentCases.Values
            .Where(value => string.Equals(value.Resolution?.ResolutionId, resolutionId, StringComparison.Ordinal))
            .ToArray();
        return matchingCases.Length == 1
            && GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(current, next, matchingCases[0]);
    }

    private static bool IsHash(string value)
        => value.Length == GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryAddProofBytes(ref long retainedBytes, long bytes)
    {
        if (bytes < 0 || retainedBytes > _maximumStoreBytes - bytes)
        {
            return false;
        }

        retainedBytes += bytes;
        return true;
    }
}
