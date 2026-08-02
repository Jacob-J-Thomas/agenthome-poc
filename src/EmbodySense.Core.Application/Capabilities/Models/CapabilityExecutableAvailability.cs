namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns a bounded executable-host availability decision.</summary>
/// <param name="Status">The availability status.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus Status, string Detail);
