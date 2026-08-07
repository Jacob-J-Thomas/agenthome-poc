using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Deterministically resolves bounded manifest dependencies against explicit governed catalog candidates.</summary>
/// <remarks>Resolution is read-only evidence production. It neither installs nor enables a capability and cannot assign it to a loop or grant authority.</remarks>
public sealed class CapabilityDependencyResolver
{
    private static readonly CapabilityId _invalidRootManifestSubjectId = CreateInvalidRootManifestSubjectId();
    private readonly CapabilityDependencyResolutionLimits _limits;

    /// <summary>Creates a resolver with conservative schema-version-1 traversal limits.</summary>
    public CapabilityDependencyResolver() : this(CapabilityDependencyResolutionLimits.Default)
    {
    }

    /// <summary>Creates a resolver with explicit bounded traversal limits.</summary>
    public CapabilityDependencyResolver(CapabilityDependencyResolutionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!limits.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        _limits = limits;
    }

    /// <summary>Resolves required and optional transitive dependencies with ordinal deterministic ordering.</summary>
    public CapabilityDependencyResolutionResult Resolve(CapabilityDependencyManifest manifest, IReadOnlyList<CapabilityDependencyCatalogCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(candidates);
        var evidence = new List<CapabilityDependencyResolutionEvidence>();
        if (!CapabilityDependencyManifestValidator.Validate(manifest).IsValid)
        {
            var subjectId = manifest.SubjectId ?? _invalidRootManifestSubjectId;
            return new CapabilityDependencyResolutionResult(false, [], [new CapabilityDependencyResolutionEvidence(subjectId, subjectId, AnyRange(), false, CapabilityDependencyResolutionOutcome.Invalid, null, "The root dependency manifest is invalid and has no usable subject identity.")]);
        }

        if (candidates.Count > _limits.MaximumCandidates)
        {
            return new CapabilityDependencyResolutionResult(false, [], [new CapabilityDependencyResolutionEvidence(manifest.SubjectId, manifest.SubjectId, AnyRange(), false, CapabilityDependencyResolutionOutcome.LimitExceeded, null, "The governed catalog candidate bound was exceeded.")]);
        }

        CapabilityDependencyResolutionContext? context = null;
        IReadOnlyDictionary<string, CapabilityDependencyCatalogCandidate> previousSelection = new Dictionary<string, CapabilityDependencyCatalogCandidate>(StringComparer.Ordinal);
        bool? previousFailed = null;
        for (var iteration = 0; iteration < _limits.MaximumDependencies; iteration++)
        {
            evidence = [];
            context = new CapabilityDependencyResolutionContext(candidates, evidence, _limits, previousSelection);
            context.ResolveManifest(manifest, 0, []);
            if (SameSelection(previousSelection, context.Selected) && previousFailed == context.Failed)
            {
                break;
            }

            previousSelection = context.Selected;
            previousFailed = context.Failed;
        }

        if (context is null)
        {
            throw new InvalidOperationException("Dependency resolution did not start.");
        }

        if (!SameSelection(previousSelection, context.Selected) || previousFailed != context.Failed)
        {
            context.ReportFixedPointLimit(manifest.SubjectId);
        }

        var selected = context.Selected.Values.Select(ToPin).OrderBy(item => item.DescriptorIdentity.Id.Value, StringComparer.Ordinal).ToArray();
        return new CapabilityDependencyResolutionResult(!context.Failed, selected, evidence.OrderBy(item => item.SubjectId.Value, StringComparer.Ordinal).ThenBy(item => item.DependencyId.Value, StringComparer.Ordinal).ThenBy(item => item.IsOptional).ToArray());
    }

    private static bool SameSelection(IReadOnlyDictionary<string, CapabilityDependencyCatalogCandidate> left, IReadOnlyDictionary<string, CapabilityDependencyCatalogCandidate> right)
    {
        return left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var candidate) && string.Equals(item.Value.Entry.Lifecycle.DescriptorIdentity.Hash.Value, candidate.Entry.Lifecycle.DescriptorIdentity.Hash.Value, StringComparison.Ordinal));
    }

    private static CapabilityResolvedPin ToPin(CapabilityDependencyCatalogCandidate candidate) => new(candidate.Entry.Lifecycle.DescriptorIdentity, candidate.Entry.Descriptor.Implementation, candidate.Entry.Descriptor.Provenance, candidate.Artifact);

    private static CapabilityVersionRange AnyRange()
    {
        _ = CapabilityVersionRange.TryParse("*", out var range, out _);
        return range!;
    }

    private static CapabilityId CreateInvalidRootManifestSubjectId()
    {
        _ = CapabilityId.TryParse("org.embodysense/invalid-root-manifest", out var id, out _);
        return id!;
    }

}
