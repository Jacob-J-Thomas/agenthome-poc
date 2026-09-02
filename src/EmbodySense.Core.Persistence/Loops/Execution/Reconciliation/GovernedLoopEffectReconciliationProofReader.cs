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
    private readonly string _root;

    internal GovernedLoopEffectReconciliationProofReader(CustomLoopArtifactPathGuard guard, string root)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _guard = guard;
        _root = root;
    }

    /// <summary>Reads every current canonical reconciliation case under the caller's shared persistence lock.</summary>
    internal bool TryReadCurrentCases(out IReadOnlyDictionary<string, GovernedLoopEffectReconciliationCase> currentCases)
    {
        currentCases = new Dictionary<string, GovernedLoopEffectReconciliationCase>(StringComparer.Ordinal);

        try
        {
            var casesByStorageKey = new Dictionary<string, List<(long Version, string Hash, GovernedLoopEffectReconciliationCase Value)>>(StringComparer.Ordinal);
            var versionFileCount = 0;
            foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
            {
                if (++versionFileCount > checked(GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases * GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions))
                {
                    return false;
                }

                var fileName = Path.GetFileName(path);
                if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseVersionFile(fileName, out var storageKey, out var version, out var hash))
                {
                    return false;
                }

                var safePath = _guard.GetFilePath(_root, fileName);
                if (_guard.GetFileLength(_root, safePath) > GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes)
                {
                    return false;
                }

                var bytes = File.ReadAllBytes(safePath);
                if (!GovernedLoopEffectReconciliationRecordCodec.TryDecode(bytes, out var value, out _)
                    || value is null
                    || value.CaseVersion != version
                    || !string.Equals(value.ContentHash, hash, StringComparison.Ordinal)
                    || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(value.CaseId), storageKey, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!casesByStorageKey.TryGetValue(storageKey, out var versions))
                {
                    if (casesByStorageKey.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases)
                    {
                        return false;
                    }

                    versions = [];
                    casesByStorageKey.Add(storageKey, versions);
                }
                if (versions.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions)
                {
                    return false;
                }
                versions.Add((version, hash, value));
            }

            var caseHeads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.head", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseHeadFile(fileName, out var storageKey)
                    || _guard.GetFileLength(_root, _guard.GetFilePath(_root, fileName)) != GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters)
                {
                    return false;
                }
                if (caseHeads.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases)
                {
                    return false;
                }

                var headPath = _guard.GetFilePath(_root, fileName);
                var head = Encoding.ASCII.GetString(File.ReadAllBytes(headPath));
                if (!IsHash(head) || !caseHeads.TryAdd(storageKey, head))
                {
                    return false;
                }
            }

            foreach (var pair in casesByStorageKey)
            {
                var versions = pair.Value.OrderBy(item => item.Version).ToArray();
                if (!caseHeads.TryGetValue(pair.Key, out var head)
                    || versions.Length == 0
                    || versions.Any(item => !string.Equals(item.Value.CaseId, versions[0].Value.CaseId, StringComparison.Ordinal))
                    || versions[0].Version != 1
                    || versions[0].Value.PreviousContentHash is not null
                    || !string.Equals(head, versions[^1].Hash, StringComparison.Ordinal))
                {
                    return false;
                }

                for (var index = 1; index < versions.Length; index++)
                {
                    if (versions[index].Version != versions[index - 1].Version + 1
                        || !GovernedLoopEffectReconciliationContractValidator.ValidateTransition(versions[index - 1].Value, versions[index].Value).IsValid)
                    {
                        return false;
                    }
                }
            }

            if (caseHeads.Count != casesByStorageKey.Count)
            {
                return false;
            }

            currentCases = casesByStorageKey.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(item => item.Version).Last().Value,
                StringComparer.Ordinal);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            currentCases = new Dictionary<string, GovernedLoopEffectReconciliationCase>(StringComparer.Ordinal);
            return false;
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
}
