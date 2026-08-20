using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Resolves bounded schedule payload bytes from one composition-owned opaque identity.</summary>
/// <remarks>
/// The governed reference is an identity, never a file-system path, URI, secret, or ambient locator.
/// Implementations must return isolated bytes from an already-authorized composition-owned source.
/// </remarks>
public interface IScheduleGovernedPayloadSource
{
    /// <summary>Resolves one opaque governed payload identity without interpreting it as a locator.</summary>
    /// <param name="governedReference">The exact canonical <c>payload/</c> identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The closed source posture and, only when available, exact isolated content evidence.</returns>
    Task<ScheduleGovernedPayloadResolution> ResolveAsync(
        string governedReference,
        CancellationToken cancellationToken = default);
}
