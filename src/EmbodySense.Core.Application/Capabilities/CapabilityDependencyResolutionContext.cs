using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Builds one bounded deterministic dependency-resolution pass from a prior exact selection.</summary>
internal sealed class CapabilityDependencyResolutionContext
{
    private readonly IReadOnlyList<CapabilityDependencyCatalogCandidate> _candidates;
    private readonly List<CapabilityDependencyResolutionEvidence> _evidence;
    private readonly CapabilityDependencyResolutionLimits _limits;
    private readonly Dictionary<string, List<CapabilityVersionRange>> _ranges = new(StringComparer.Ordinal);
    private int _dependencyCount;

    public CapabilityDependencyResolutionContext(IReadOnlyList<CapabilityDependencyCatalogCandidate> candidates, List<CapabilityDependencyResolutionEvidence> evidence, CapabilityDependencyResolutionLimits limits, IReadOnlyDictionary<string, CapabilityDependencyCatalogCandidate> preferredSelection)
    {
        _candidates = candidates;
        _evidence = evidence;
        _limits = limits;
        PreferredSelection = preferredSelection;
    }

    public Dictionary<string, CapabilityDependencyCatalogCandidate> Selected { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, CapabilityDependencyCatalogCandidate> PreferredSelection { get; }

    public bool Failed { get; private set; }

    public void ResolveManifest(CapabilityDependencyManifest manifest, int depth, HashSet<string> ancestors)
    {
        if (depth > _limits.MaximumDepth)
        {
            Fail(manifest.SubjectId, manifest.SubjectId, AnyRange(), false, CapabilityDependencyResolutionOutcome.LimitExceeded, "The dependency depth bound was exceeded.");
            return;
        }

        var nextAncestors = new HashSet<string>(ancestors, StringComparer.Ordinal) { manifest.SubjectId.Value };
        foreach (var pair in new[] { (Dependencies: manifest.Required, Optional: false), (Dependencies: manifest.Optional, Optional: true) })
        {
            foreach (var dependency in pair.Dependencies.OrderBy(item => item.CapabilityId.Value, StringComparer.Ordinal))
            {
                ResolveDependency(manifest.SubjectId, dependency, pair.Optional, depth, nextAncestors);
            }
        }
    }

    public void ReportFixedPointLimit(CapabilityId subjectId)
    {
        Fail(subjectId, subjectId, AnyRange(), false, CapabilityDependencyResolutionOutcome.LimitExceeded, "The dependency graph did not converge within the bounded fixed-point traversal limit.");
    }

    private void ResolveDependency(CapabilityId subjectId, CapabilityDependency dependency, bool optional, int depth, HashSet<string> ancestors)
    {
        if (depth >= _limits.MaximumDepth)
        {
            Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.LimitExceeded, "The dependency depth bound was exceeded.");
            return;
        }

        if (++_dependencyCount > _limits.MaximumDependencies)
        {
            Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.LimitExceeded, "The dependency count bound was exceeded.");
            return;
        }

        if (ancestors.Contains(dependency.CapabilityId.Value))
        {
            Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Cyclic, "The dependency graph contains a cycle.");
            return;
        }

        if (!_ranges.TryGetValue(dependency.CapabilityId.Value, out var ranges))
        {
            ranges = [];
            _ranges.Add(dependency.CapabilityId.Value, ranges);
        }

        if (!ranges.Any(item => item.Equals(dependency.CompatibleVersionRange)))
        {
            ranges.Add(dependency.CompatibleVersionRange);
        }

        var group = _candidates.Where(item => item.Entry?.Descriptor?.Id is not null && item.Entry.Descriptor.Id.Equals(dependency.CapabilityId)).ToArray();
        if (group.Length == 0)
        {
            ObserveUnavailable(subjectId, dependency, optional, CapabilityDependencyResolutionOutcome.Missing, "No governed catalog candidate exists for this canonical capability id.");
            return;
        }

        var compatible = group.Where(item => ranges.All(range => range.Contains(item.Entry.Descriptor.Version))).ToArray();
        if (compatible.Length == 0)
        {
            ObserveUnavailable(subjectId, dependency, optional, CapabilityDependencyResolutionOutcome.Incompatible, "No catalog candidate satisfies the declared compatible-version range.");
            return;
        }

        if (HasConflictingExactEvidence(compatible))
        {
            Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Conflict, "Equal-version candidates have conflicting exact descriptor or provenance evidence.");
            return;
        }

        var trusted = compatible.Where(IsResolvable).OrderByDescending(item => item.Entry.Descriptor.Version).ThenBy(item => item.Entry.Lifecycle.DescriptorIdentity.Hash.Value, StringComparer.Ordinal).ToArray();
        if (trusted.Length == 0)
        {
            Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Untrusted, "Compatible candidates are unavailable, unverified, or have mismatched integrity evidence.");
            return;
        }

        var candidate = PreferredSelection.TryGetValue(dependency.CapabilityId.Value, out var preferred) && trusted.Any(item => SameExactCandidate(item, preferred)) ? preferred : trusted[0];
        if (candidate.Dependencies is not null)
        {
            if (!CapabilityDependencyManifestValidator.Validate(candidate.Dependencies).IsValid)
            {
                Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Invalid, "The selected candidate dependency manifest is invalid.");
                return;
            }
            if (!candidate.Dependencies.SubjectId.Equals(candidate.Entry.Descriptor.Id))
            {
                Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Invalid, "The selected candidate dependency manifest belongs to another capability.");
                return;
            }
        }

        var pin = new CapabilityResolvedPin(candidate.Entry.Lifecycle.DescriptorIdentity, candidate.Entry.Descriptor.Implementation, candidate.Entry.Descriptor.Provenance, candidate.Artifact);
        Selected[dependency.CapabilityId.Value] = candidate;
        _evidence.Add(new CapabilityDependencyResolutionEvidence(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, optional, CapabilityDependencyResolutionOutcome.Selected, pin, "A server-verified installed and available catalog candidate was selected."));
        if (candidate.Dependencies is not null)
        {
            ResolveManifest(candidate.Dependencies, depth + 1, ancestors);
        }
    }

    private static bool HasConflictingExactEvidence(IEnumerable<CapabilityDependencyCatalogCandidate> candidates)
    {
        return candidates.GroupBy(item => item.Entry.Descriptor.Version.Value, StringComparer.Ordinal).Any(group => group.Select(ExactEvidence).Distinct(StringComparer.Ordinal).Skip(1).Any());
    }

    private static bool SameExactCandidate(CapabilityDependencyCatalogCandidate left, CapabilityDependencyCatalogCandidate right) => string.Equals(ExactEvidence(left), ExactEvidence(right), StringComparison.Ordinal);

    private static string ExactEvidence(CapabilityDependencyCatalogCandidate candidate)
    {
        var dependencies = candidate.Dependencies is null ? "none" : CapabilityDependencyManifestHash.TryCompute(candidate.Dependencies, out var hash, out _) ? hash!.Value : "invalid";
        return candidate.Entry.Lifecycle.DescriptorIdentity.Hash.Value + "\n" + candidate.Entry.Descriptor.Provenance.SourceUri + "\n" + candidate.Entry.Descriptor.Provenance.SourceRevision + "\n" + candidate.Entry.Descriptor.Provenance.Integrity?.Value + "\n" + candidate.Artifact.Checksum?.Value + "\n" + candidate.Artifact.Signature + "\n" + dependencies;
    }

    private static bool IsResolvable(CapabilityDependencyCatalogCandidate candidate)
    {
        var lifecycle = candidate.Entry.Lifecycle;
        var declaredIntegrity = candidate.Entry.Descriptor.Provenance.Integrity;
        return lifecycle.Declaration == CapabilityDeclarationState.Declared && lifecycle.Installation == CapabilityInstallationState.Installed && lifecycle.Health is CapabilityHealthState.Healthy or CapabilityHealthState.Degraded && lifecycle.Retirement != CapabilityRetirementState.Removed && lifecycle.Trust == CapabilityTrustState.Verified && (candidate.Artifact.Checksum is null || declaredIntegrity is null || candidate.Artifact.Checksum.FixedTimeEquals(declaredIntegrity));
    }

    private void ObserveUnavailable(CapabilityId subjectId, CapabilityDependency dependency, bool optional, CapabilityDependencyResolutionOutcome requiredOutcome, string detail)
    {
        if (optional)
        {
            _evidence.Add(new CapabilityDependencyResolutionEvidence(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, true, CapabilityDependencyResolutionOutcome.OmittedOptional, null, detail));
            return;
        }

        Fail(subjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, false, requiredOutcome, detail);
    }

    private void Fail(CapabilityId subjectId, CapabilityId dependencyId, CapabilityVersionRange range, bool optional, CapabilityDependencyResolutionOutcome outcome, string detail)
    {
        Failed = true;
        _evidence.Add(new CapabilityDependencyResolutionEvidence(subjectId, dependencyId, range, optional, outcome, null, detail));
    }

    private static CapabilityVersionRange AnyRange()
    {
        _ = CapabilityVersionRange.TryParse("*", out var range, out _);
        return range!;
    }
}
