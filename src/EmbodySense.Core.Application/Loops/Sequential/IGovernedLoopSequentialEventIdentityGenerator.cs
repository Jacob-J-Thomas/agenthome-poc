namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Generates only append-only event identities for canonical sequential materialization.</summary>
/// <remarks>The admitted run identity is supplied exclusively by the committed canonical admission receipt.</remarks>
public interface IGovernedLoopSequentialEventIdentityGenerator
{
    /// <summary>Creates one canonical event identifier.</summary>
    string NewEventId();
}
