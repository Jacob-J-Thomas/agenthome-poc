using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Composes bounded domain adapters into one deterministic fail-closed dependent index.</summary>
public sealed class CapabilityDependentIndex : ICapabilityDependentIndex
{
    private const int MaximumDependents = 2_048;
    private readonly IReadOnlyList<ICapabilityDependentIndexSource> _sources;

    /// <summary>Creates an index over current adapters and explicit future role and schedule registration seams.</summary>
    /// <param name="currentSources">Current loop, skill, and package adapters.</param>
    /// <param name="roleSource">An optional future role adapter.</param>
    /// <param name="scheduleSource">An optional future schedule adapter.</param>
    public CapabilityDependentIndex(IEnumerable<ICapabilityDependentIndexSource> currentSources, IRoleCapabilityDependentIndexSource? roleSource = null, IScheduleCapabilityDependentIndexSource? scheduleSource = null)
    {
        ArgumentNullException.ThrowIfNull(currentSources);
        var sources = currentSources.ToList();
        if (roleSource is not null)
        {
            sources.Add(roleSource);
        }
        if (scheduleSource is not null)
        {
            sources.Add(scheduleSource);
        }
        if (sources.Count == 0 || sources.Any(source => source is null) || sources.Select(source => source.Name).Any(string.IsNullOrWhiteSpace) || sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw new ArgumentException("Dependent sources require unique nonblank names.", nameof(currentSources));
        }
        _sources = Array.AsReadOnly(sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToArray());
    }

    /// <inheritdoc />
    public async Task<CapabilityDependentIndexSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dependents = new List<CapabilityDependent>();
            foreach (var source in _sources)
            {
                var slice = await source.ReadAsync(cancellationToken);
                if (slice is null || dependents.Count + slice.Count > MaximumDependents)
                {
                    return Unavailable($"Dependent source '{source.Name}' exceeded the bounded index contract.");
                }
                if (source is IRoleCapabilityDependentIndexSource && slice.Any(dependent => dependent is null || dependent.Kind != CapabilityDependentKind.Role) || source is IScheduleCapabilityDependentIndexSource && slice.Any(dependent => dependent is null || dependent.Kind != CapabilityDependentKind.Schedule) || source is not IRoleCapabilityDependentIndexSource and not IScheduleCapabilityDependentIndexSource && slice.Any(dependent => dependent is not null && dependent.Kind is CapabilityDependentKind.Role or CapabilityDependentKind.Schedule))
                {
                    return Unavailable($"Dependent source '{source.Name}' attempted to cross a domain registration seam.");
                }
                dependents.AddRange(slice);
            }

            if (!Validate(dependents))
            {
                return Unavailable("At least one dependent was invalid, duplicated, forged, or outside the bounded index contract.");
            }
            var ordered = dependents.OrderBy(dependent => dependent.Kind).ThenBy(dependent => dependent.Identity, StringComparer.Ordinal).ToArray();
            var hash = ComputeHash(ordered);
            return new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, hash, ordered, "The complete registered dependent set was captured deterministically.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return Unavailable("At least one registered dependent source is unavailable.");
        }
    }

    private static bool Validate(IReadOnlyList<CapabilityDependent> dependents)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependent in dependents)
        {
            if (dependent is null || !Enum.IsDefined(dependent.Kind) || !Enum.IsDefined(dependent.AuthorityPosture) || !IsSafe(dependent.Identity, 256) || !IsSafe(dependent.Revision, 256) || !CapabilityDependencyManifestValidator.Validate(dependent.Manifest).IsValid || !identities.Add($"{(int)dependent.Kind}:{dependent.Identity}"))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeHash(IEnumerable<CapabilityDependent> dependents)
    {
        var builder = new StringBuilder("capability-dependent-index-v1\n");
        foreach (var dependent in dependents)
        {
            _ = CapabilityDependencyManifestHash.TryCompute(dependent.Manifest, out var manifestHash, out _);
            Append(builder, ((int)dependent.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, dependent.Identity);
            Append(builder, dependent.Revision);
            Append(builder, ((int)dependent.AuthorityPosture).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, manifestHash!.Value);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    private static bool IsSafe(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value.All(character => character >= (char)0x20 && character != (char)0x7f);

    private static CapabilityDependentIndexSnapshot Unavailable(string detail) => new(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], detail);
}
