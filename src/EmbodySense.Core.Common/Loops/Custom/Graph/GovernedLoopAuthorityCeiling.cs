using System.Collections.ObjectModel;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Represents a non-granting maximum set of authority capability identifiers.</summary>
/// <remarks>The ceiling can only constrain authority granted elsewhere. Possession of this value grants nothing.</remarks>
public sealed record GovernedLoopAuthorityCeiling
{
    private GovernedLoopAuthorityCeiling(string[] capabilityIds)
    {
        CapabilityIds = new ReadOnlyCollection<string>(capabilityIds);
    }

    /// <summary>Gets the canonical ordinal capability set.</summary>
    /// <value>The sorted immutable capability identifiers.</value>
    public IReadOnlyList<string> CapabilityIds { get; }

    /// <summary>Creates a validated non-granting authority ceiling.</summary>
    /// <param name="capabilityIds">The capability identifiers, which must be unique and canonical.</param>
    /// <returns>An immutable ceiling in canonical order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="capabilityIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when an identifier is invalid or duplicated, or the maximum is exceeded.</exception>
    public static GovernedLoopAuthorityCeiling Create(IEnumerable<string> capabilityIds)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        var values = capabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities + 1).ToArray();
        if (values.Length > CustomLoopLimits.MaxGraphAuthorityCapabilities)
        {
            throw new ArgumentException($"Authority ceilings cannot contain more than {CustomLoopLimits.MaxGraphAuthorityCapabilities} capabilities.", nameof(capabilityIds));
        }

        if (values.Any(value => !CapabilityId.TryParse(value, out _, out _)))
        {
            throw new ArgumentException("Authority ceilings require canonical lowercase provider/path capability identifiers.", nameof(capabilityIds));
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("Authority ceiling capability identifiers must be unique under ordinal comparison.", nameof(capabilityIds));
        }

        return new GovernedLoopAuthorityCeiling(values.Order(StringComparer.Ordinal).ToArray());
    }
}
