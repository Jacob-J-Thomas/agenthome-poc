using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Stores bounded, process-local, server-composed Human Input lifecycle candidates.</summary>
/// <remarks>The registry is a short-lived proposal cache, not a lifecycle ledger or authority source. A process restart
/// intentionally invalidates every key. Each lookup revalidates the complete candidate and every caller-supplied binding.
/// Once a lifecycle operation is committed, the durable operation identifier, exact request reference, operation kind, and
/// command evidence are authoritative for replay; a process-local candidate key is not. A pre-commit lookup still requires
/// the exact opaque key and rejects a different key.</remarks>
public sealed class HumanInputSupersedeCandidateRegistry : IHumanInputSupersedeCandidateRegistry
{
    private const int MaximumEntries = 256;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _operationKeys = new(StringComparer.Ordinal);

    /// <summary>Creates a bounded registry using the system trusted clock unless a composed clock is supplied.</summary>
    /// <param name="timeProvider">The trusted clock used for expiry and capacity purging.</param>
    public HumanInputSupersedeCandidateRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool TryRegister(HumanInputSupersedeCandidateRegistration candidate, out string candidateKey)
        => TryRegister(candidate, out candidateKey, out _);

    /// <inheritdoc />
    public bool TryRegister(HumanInputSupersedeCandidateRegistration candidate, out string candidateKey, out HumanInputSupersedePreparationStatus status)
    {
        candidateKey = string.Empty;
        if (candidate?.Kind == HumanInputRequestLifecycleOperationKind.Reroute)
        {
            status = HumanInputSupersedePreparationStatus.Invalid;
            return false;
        }

        if (!IsValidRegistration(candidate, out var capturedCandidate))
        {
            status = HumanInputSupersedePreparationStatus.Invalid;
            return false;
        }

        var validCandidate = candidate!;
        if (!TryGetUtcNow(out var now))
        {
            status = HumanInputSupersedePreparationStatus.Unavailable;
            return false;
        }
        var binding = BindingKey(validCandidate);
        lock (_gate)
        {
            Purge(now);
            if (_operationKeys.TryGetValue(binding, out var existingKey)
                && _entries.TryGetValue(existingKey, out var existing)
                && existing.Kind == validCandidate.Kind
                && existing.ExpiresAtUtc == validCandidate.ExpiresAtUtc
                && string.Equals(existing.CandidateRequest.RequestHash, capturedCandidate!.RequestHash, StringComparison.Ordinal)
                && Equals(existing.GrantReference, validCandidate.GrantReference))
            {
                candidateKey = existingKey;
                status = HumanInputSupersedePreparationStatus.Ready;
                return true;
            }

            if (validCandidate.Kind != HumanInputRequestLifecycleOperationKind.Supersede
                && (validCandidate.ExpiresAtUtc.Offset != TimeSpan.Zero
                    || validCandidate.ExpiresAtUtc - now < HumanInputLifecycleCandidateLimits.MinCandidateLifetime
                    || validCandidate.ExpiresAtUtc - now > HumanInputLifecycleCandidateLimits.MaxCandidateLifetime))
            {
                status = HumanInputSupersedePreparationStatus.Invalid;
                return false;
            }

            if (_operationKeys.ContainsKey(binding))
            {
                status = HumanInputSupersedePreparationStatus.Conflict;
                return false;
            }

            var groupEntries = _entries.Values.Where(entry => string.Equals(GroupKey(entry), GroupKey(validCandidate), StringComparison.Ordinal)).ToArray();
            if (groupEntries.Any(entry => !string.Equals(entry.PreparationHash, validCandidate.PreparationHash, StringComparison.Ordinal))
                || validCandidate.Kind != HumanInputRequestLifecycleOperationKind.Reroute && groupEntries.Length > 0
                || validCandidate.Kind == HumanInputRequestLifecycleOperationKind.Reroute && groupEntries.Length >= HumanInputLifecycleCandidateLimits.MaxRerouteOptions)
            {
                status = validCandidate.Kind == HumanInputRequestLifecycleOperationKind.Reroute && groupEntries.Length >= HumanInputLifecycleCandidateLimits.MaxRerouteOptions
                    ? HumanInputSupersedePreparationStatus.LimitExceeded
                    : HumanInputSupersedePreparationStatus.Conflict;
                return false;
            }

            if (_entries.Count >= MaximumEntries)
            {
                status = HumanInputSupersedePreparationStatus.LimitExceeded;
                return false;
            }

            if (!TryGenerateKeys(1, out var generatedKeys))
            {
                status = HumanInputSupersedePreparationStatus.Unavailable;
                return false;
            }

            candidateKey = generatedKeys[0];
            _entries[candidateKey] = new Entry(
                validCandidate.WorkspaceId,
                validCandidate.Actor,
                validCandidate.OperationId,
                validCandidate.RequestId,
                validCandidate.ExpectedLifecycleVersion,
                validCandidate.ExpectedRequest with { },
                capturedCandidate!,
                validCandidate.GrantReference,
                validCandidate.ExpiresAtUtc,
                validCandidate.Kind,
                validCandidate.PreparationHash);
            _operationKeys[binding] = candidateKey;
            status = HumanInputSupersedePreparationStatus.Ready;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryRegisterGroup(IReadOnlyList<HumanInputSupersedeCandidateRegistration> candidates, out IReadOnlyList<string> candidateKeys, out HumanInputSupersedePreparationStatus status)
    {
        candidateKeys = Array.Empty<string>();
        if (candidates is null || candidates.Count == 0)
        {
            status = HumanInputSupersedePreparationStatus.Invalid;
            return false;
        }

        if (candidates.Count > HumanInputLifecycleCandidateLimits.MaxRerouteOptions)
        {
            status = HumanInputSupersedePreparationStatus.LimitExceeded;
            return false;
        }

        var prepared = new (HumanInputSupersedeCandidateRegistration Registration, HumanInputRequest Candidate, string Binding, string Group)[candidates.Count];
        if (!TryGetUtcNow(out var registrationNow))
        {
            status = HumanInputSupersedePreparationStatus.Unavailable;
            return false;
        }
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!IsValidRegistration(candidate, out var captured)
                || candidate.Kind != HumanInputRequestLifecycleOperationKind.Reroute
                || captured is null
                || candidate.PreparationHash is null
                || candidate.ExpiresAtUtc.Offset != TimeSpan.Zero)
            {
                status = HumanInputSupersedePreparationStatus.Invalid;
                return false;
            }

            prepared[index] = (candidate, captured, BindingKey(candidate), GroupKey(candidate));
        }

        if (prepared.Any(item => item.Registration.ExpiresAtUtc <= registrationNow
            || item.Registration.ExpiresAtUtc - registrationNow > HumanInputLifecycleCandidateLimits.MaxCandidateLifetime)
            || prepared.Any(item => !HumanInputRequestLifecycleValidator.ValidateReference(item.Registration.ExpectedRequest).IsValid))
        {
            status = HumanInputSupersedePreparationStatus.Invalid;
            return false;
        }

        if (prepared.Select(item => item.Group).Distinct(StringComparer.Ordinal).Count() != 1
            || prepared.Select(item => item.Registration.PreparationHash).Distinct(StringComparer.Ordinal).Count() != 1
            || prepared.Select(item => item.Binding).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            status = HumanInputSupersedePreparationStatus.Conflict;
            return false;
        }

        var groupKey = prepared[0].Group;
        var preparationHash = prepared[0].Registration.PreparationHash;
        lock (_gate)
        {
            if (!TryGetUtcNow(out var now))
            {
                status = HumanInputSupersedePreparationStatus.Unavailable;
                return false;
            }
            Purge(now);
            var existingGroup = _entries.Values.Where(entry => string.Equals(GroupKey(entry), groupKey, StringComparison.Ordinal)).ToArray();
            if (existingGroup.Any(entry => !string.Equals(entry.PreparationHash, preparationHash, StringComparison.Ordinal)))
            {
                status = HumanInputSupersedePreparationStatus.Conflict;
                return false;
            }

            var resolvedKeys = new string[candidates.Count];
            var allReplayed = true;
            for (var index = 0; index < prepared.Length; index++)
            {
                if (!_operationKeys.TryGetValue(prepared[index].Binding, out var existingKey)
                    || !_entries.TryGetValue(existingKey, out var existing)
                    || !Matches(existing, prepared[index].Registration, prepared[index].Candidate))
                {
                    allReplayed = false;
                    continue;
                }

                resolvedKeys[index] = existingKey;
            }

            if (allReplayed && existingGroup.Length == candidates.Count)
            {
                candidateKeys = Array.AsReadOnly(resolvedKeys);
                status = HumanInputSupersedePreparationStatus.Ready;
                return true;
            }

            if (existingGroup.Length > 0 || resolvedKeys.Any(key => !string.IsNullOrEmpty(key)))
            {
                status = HumanInputSupersedePreparationStatus.Conflict;
                return false;
            }

            if (prepared.Any(item => item.Registration.ExpiresAtUtc - now < HumanInputLifecycleCandidateLimits.MinCandidateLifetime
                || item.Registration.ExpiresAtUtc - now > HumanInputLifecycleCandidateLimits.MaxCandidateLifetime))
            {
                status = HumanInputSupersedePreparationStatus.Invalid;
                return false;
            }

            if (_entries.Count > MaximumEntries - candidates.Count)
            {
                status = HumanInputSupersedePreparationStatus.LimitExceeded;
                return false;
            }

            if (!TryGenerateKeys(prepared.Length, out var generatedKeys))
            {
                status = HumanInputSupersedePreparationStatus.Unavailable;
                return false;
            }

            for (var index = 0; index < prepared.Length; index++)
            {
                var key = generatedKeys[index];
                var registration = prepared[index].Registration;
                _entries[key] = new Entry(
                    registration.WorkspaceId,
                    registration.Actor,
                    registration.OperationId,
                    registration.RequestId,
                    registration.ExpectedLifecycleVersion,
                    registration.ExpectedRequest with { },
                    prepared[index].Candidate,
                    registration.GrantReference,
                    registration.ExpiresAtUtc,
                    registration.Kind,
                    registration.PreparationHash);
                _operationKeys[prepared[index].Binding] = key;
            }

            candidateKeys = Array.AsReadOnly(generatedKeys);
            status = HumanInputSupersedePreparationStatus.Ready;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryResolve(
        string candidateKey,
        string workspaceId,
        string actor,
        string operationId,
        string requestId,
        long expectedLifecycleVersion,
        string expectedRequestVersionId,
        string expectedRequestHash,
        DateTimeOffset now,
        out HumanInputSupersedeCandidateResolution? resolution)
        => TryResolve(HumanInputRequestLifecycleOperationKind.Supersede, candidateKey, workspaceId, actor, operationId, requestId, expectedLifecycleVersion, expectedRequestVersionId, expectedRequestHash, now, out resolution);

    /// <inheritdoc />
    public bool TryResolve(
        HumanInputRequestLifecycleOperationKind kind,
        string candidateKey,
        string workspaceId,
        string actor,
        string operationId,
        string requestId,
        long expectedLifecycleVersion,
        string expectedRequestVersionId,
        string expectedRequestHash,
        DateTimeOffset now,
        out HumanInputSupersedeCandidateResolution? resolution)
    {
        resolution = null;
        if (string.IsNullOrWhiteSpace(candidateKey)
            || string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(actor)
            || !HumanInputIdentifier.IsValid(operationId)
            || string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(expectedRequestVersionId)
            || string.IsNullOrWhiteSpace(expectedRequestHash)
            || now == default
            || kind is not (HumanInputRequestLifecycleOperationKind.Supersede or HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend))
        {
            return false;
        }

        lock (_gate)
        {
            Purge(now);
            if (!_entries.TryGetValue(candidateKey, out var entry))
            {
                return false;
            }

            try
            {
                if (entry.ExpiresAtUtc <= now
                    || !string.Equals(entry.WorkspaceId, workspaceId, StringComparison.Ordinal)
                    || !string.Equals(entry.Actor, actor, StringComparison.Ordinal)
                    || !string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)
                    || entry.Kind != kind
                    || !string.Equals(entry.RequestId, requestId, StringComparison.Ordinal)
                    || entry.ExpectedLifecycleVersion != expectedLifecycleVersion
                    || !string.Equals(entry.ExpectedRequest.RequestVersionId, expectedRequestVersionId, StringComparison.Ordinal)
                    || !string.Equals(entry.ExpectedRequest.RequestHash, expectedRequestHash, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!HumanInputRequestLifecycleValidator.ValidateReference(entry.ExpectedRequest).IsValid)
                {
                    RemoveEntry(candidateKey, entry);
                    return false;
                }

                if (!HumanInputRequestSnapshot.TryCapture(entry.CandidateRequest, out var candidate, out _)
                    || candidate is null
                    || !HumanInputValidator.ValidateRequest(candidate).IsValid
                    || !HumanInputRequestHash.Matches(candidate)
                    || candidate.Binding is null
                    || !string.Equals(candidate.Binding.WorkspaceId, workspaceId, StringComparison.Ordinal))
                {
                    RemoveEntry(candidateKey, entry);
                    return false;
                }

                resolution = new HumanInputSupersedeCandidateResolution(candidate, entry.GrantReference);
                return true;
            }
            catch (Exception)
            {
                RemoveEntry(candidateKey, entry);
                return false;
            }
        }
    }

    private void RemoveEntry(string candidateKey, Entry entry)
    {
        _entries.Remove(candidateKey);
        try
        {
            _operationKeys.Remove(BindingKey(entry));
        }
        catch (Exception)
        {
            foreach (var pair in _operationKeys.Where(pair => string.Equals(pair.Value, candidateKey, StringComparison.Ordinal)).ToArray())
            {
                _operationKeys.Remove(pair.Key);
            }
        }
    }

    private static bool Matches(Entry existing, HumanInputSupersedeCandidateRegistration registration, HumanInputRequest candidate)
        => existing.Kind == registration.Kind
            && existing.ExpiresAtUtc == registration.ExpiresAtUtc
            && string.Equals(existing.PreparationHash, registration.PreparationHash, StringComparison.Ordinal)
            && string.Equals(existing.CandidateRequest.RequestHash, candidate.RequestHash, StringComparison.Ordinal)
            && Equals(existing.GrantReference, registration.GrantReference)
            && string.Equals(existing.WorkspaceId, registration.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(existing.Actor, registration.Actor, StringComparison.Ordinal)
            && string.Equals(existing.OperationId, registration.OperationId, StringComparison.Ordinal)
            && string.Equals(existing.RequestId, registration.RequestId, StringComparison.Ordinal)
            && existing.ExpectedLifecycleVersion == registration.ExpectedLifecycleVersion
            && Equals(existing.ExpectedRequest, registration.ExpectedRequest);

    private void Purge(DateTimeOffset now)
    {
        foreach (var pair in _entries.Where(pair => pair.Value.ExpiresAtUtc <= now).ToArray())
        {
            _entries.Remove(pair.Key);
            _operationKeys.Remove(BindingKey(pair.Value));
        }
    }

    private static bool IsValidRegistration(HumanInputSupersedeCandidateRegistration? candidate, out HumanInputRequest? captured)
    {
        captured = null;
        if (candidate is null
            || string.IsNullOrWhiteSpace(candidate.WorkspaceId)
            || string.IsNullOrWhiteSpace(candidate.Actor)
            || !HumanInputIdentifier.IsValid(candidate.OperationId)
            || string.IsNullOrWhiteSpace(candidate.RequestId)
            || candidate.ExpectedLifecycleVersion < 1
            || candidate.ExpiresAtUtc == default
            || candidate.ExpectedRequest is null
            || candidate.CandidateRequest is null
            || !string.Equals(candidate.ExpectedRequest.RequestId, candidate.RequestId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(candidate.ExpectedRequest.RequestHash)
            || !HumanInputRequestLifecycleValidator.ValidateReference(candidate.ExpectedRequest).IsValid
            || candidate.GrantReference is null
            || candidate.CandidateRequest.Binding is null
            || !string.Equals(candidate.CandidateRequest.Binding.WorkspaceId, candidate.WorkspaceId, StringComparison.Ordinal)
            || candidate.Kind is not (HumanInputRequestLifecycleOperationKind.Supersede or HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend)
            || candidate.Kind is HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend && !IsSha256(candidate.PreparationHash)
            || candidate.Kind == HumanInputRequestLifecycleOperationKind.Supersede && string.Equals(candidate.CandidateRequest.RequestId, candidate.RequestId, StringComparison.Ordinal)
            || candidate.Kind is HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend && !string.Equals(candidate.CandidateRequest.RequestId, candidate.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!HumanInputRequestSnapshot.TryCapture(candidate.CandidateRequest, out captured, out _)
            || captured is null
            || !HumanInputValidator.ValidateRequest(captured).IsValid
            || !HumanInputRequestHash.Matches(captured))
        {
            captured = null;
            return false;
        }

        return true;
    }

    private static string BindingKey(HumanInputSupersedeCandidateRegistration candidate)
        => string.Join("\u001f", candidate.Kind, candidate.WorkspaceId, candidate.Actor, candidate.OperationId, candidate.RequestId, candidate.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.ExpectedRequest.RequestVersionId, candidate.ExpectedRequest.RequestHash, candidate.PreparationHash ?? string.Empty, candidate.CandidateRequest.RequestHash);

    private static string BindingKey(Entry entry)
        => string.Join("\u001f", entry.Kind, entry.WorkspaceId, entry.Actor, entry.OperationId, entry.RequestId, entry.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.ExpectedRequest.RequestVersionId, entry.ExpectedRequest.RequestHash, entry.PreparationHash ?? string.Empty, entry.CandidateRequest.RequestHash);

    private static string GroupKey(HumanInputSupersedeCandidateRegistration candidate)
        => string.Join("\u001f", candidate.Kind, candidate.WorkspaceId, candidate.Actor, candidate.OperationId, candidate.RequestId, candidate.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.ExpectedRequest.RequestVersionId, candidate.ExpectedRequest.RequestHash);

    private static string GroupKey(Entry entry)
        => string.Join("\u001f", entry.Kind, entry.WorkspaceId, entry.Actor, entry.OperationId, entry.RequestId, entry.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.ExpectedRequest.RequestVersionId, entry.ExpectedRequest.RequestHash);

    private static bool IsSha256(string? value)
        => value is { Length: HumanInputLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryGenerateKeys(int count, out string[] keys)
    {
        keys = Array.Empty<string>();
        if (count < 1)
        {
            return false;
        }

        try
        {
            var keySet = new HashSet<string>(StringComparer.Ordinal);
            keys = new string[count];
            for (var index = 0; index < count; index++)
            {
                var accepted = false;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var generated = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    if (keySet.Add(generated) && !_entries.ContainsKey(generated))
                    {
                        keys[index] = generated;
                        accepted = true;
                        break;
                    }
                }

                if (!accepted)
                {
                    keys = Array.Empty<string>();
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            keys = Array.Empty<string>();
            return false;
        }
    }

    private bool TryGetUtcNow(out DateTimeOffset now)
    {
        try
        {
            now = _timeProvider.GetUtcNow();
            return now != default;
        }
        catch (Exception)
        {
            now = default;
            return false;
        }
    }

    private sealed record Entry(
        string WorkspaceId,
        string Actor,
        string OperationId,
        string RequestId,
        long ExpectedLifecycleVersion,
        HumanInputRequestReference ExpectedRequest,
        HumanInputRequest CandidateRequest,
        EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference GrantReference,
        DateTimeOffset ExpiresAtUtc,
        HumanInputRequestLifecycleOperationKind Kind,
        string? PreparationHash);
}
