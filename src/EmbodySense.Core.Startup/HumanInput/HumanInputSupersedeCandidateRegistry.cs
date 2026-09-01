using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Stores bounded, process-local, server-composed Human Input supersede candidates.</summary>
/// <remarks>The registry is a short-lived proposal cache, not a lifecycle ledger or authority source. A process restart
/// intentionally invalidates every key. Each lookup revalidates the complete candidate and every caller-supplied binding.</remarks>
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
    {
        candidateKey = string.Empty;
        if (!IsValidRegistration(candidate, out var capturedCandidate))
        {
            return false;
        }

        var binding = BindingKey(candidate);
        lock (_gate)
        {
            Purge(_timeProvider.GetUtcNow());
            if (_operationKeys.TryGetValue(binding, out var existingKey)
                && _entries.TryGetValue(existingKey, out var existing)
                && existing.ExpiresAtUtc == candidate.ExpiresAtUtc
                && string.Equals(existing.CandidateRequest.RequestHash, capturedCandidate!.RequestHash, StringComparison.Ordinal)
                && Equals(existing.GrantReference, candidate.GrantReference))
            {
                candidateKey = existingKey;
                return true;
            }

            if (_operationKeys.ContainsKey(binding) || _entries.Count >= MaximumEntries)
            {
                return false;
            }

            candidateKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _entries[candidateKey] = new Entry(
                candidate.WorkspaceId,
                candidate.Actor,
                candidate.OperationId,
                candidate.RequestId,
                candidate.ExpectedLifecycleVersion,
                candidate.ExpectedRequest with { },
                capturedCandidate!,
                candidate.GrantReference,
                candidate.ExpiresAtUtc);
            _operationKeys[binding] = candidateKey;
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
    {
        resolution = null;
        if (string.IsNullOrWhiteSpace(candidateKey)
            || string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(actor)
            || !HumanInputIdentifier.IsValid(operationId)
            || string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(expectedRequestVersionId)
            || string.IsNullOrWhiteSpace(expectedRequestHash)
            || now == default)
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
                    || !string.Equals(entry.RequestId, requestId, StringComparison.Ordinal)
                    || entry.ExpectedLifecycleVersion != expectedLifecycleVersion
                    || !string.Equals(entry.ExpectedRequest.RequestVersionId, expectedRequestVersionId, StringComparison.Ordinal)
                    || !string.Equals(entry.ExpectedRequest.RequestHash, expectedRequestHash, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!HumanInputRequestSnapshot.TryCapture(entry.CandidateRequest, out var candidate, out _)
                    || candidate is null
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
            || candidate.GrantReference is null
            || candidate.CandidateRequest.Binding is null
            || !string.Equals(candidate.CandidateRequest.Binding.WorkspaceId, candidate.WorkspaceId, StringComparison.Ordinal)
            || string.Equals(candidate.CandidateRequest.RequestId, candidate.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!HumanInputRequestSnapshot.TryCapture(candidate.CandidateRequest, out captured, out _)
            || captured is null
            || !HumanInputRequestHash.Matches(captured))
        {
            captured = null;
            return false;
        }

        return true;
    }

    private static string BindingKey(HumanInputSupersedeCandidateRegistration candidate)
        => string.Join("\u001f", candidate.WorkspaceId, candidate.Actor, candidate.OperationId, candidate.RequestId, candidate.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.ExpectedRequest.RequestVersionId, candidate.ExpectedRequest.RequestHash);

    private static string BindingKey(Entry entry)
        => string.Join("\u001f", entry.WorkspaceId, entry.Actor, entry.OperationId, entry.RequestId, entry.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.ExpectedRequest.RequestVersionId, entry.ExpectedRequest.RequestHash);

    private sealed record Entry(
        string WorkspaceId,
        string Actor,
        string OperationId,
        string RequestId,
        long ExpectedLifecycleVersion,
        HumanInputRequestReference ExpectedRequest,
        HumanInputRequest CandidateRequest,
        EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference GrantReference,
        DateTimeOffset ExpiresAtUtc);
}
