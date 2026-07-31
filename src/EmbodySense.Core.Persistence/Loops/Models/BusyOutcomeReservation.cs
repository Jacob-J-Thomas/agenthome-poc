namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a busy outcome reservation.
/// </summary>
/// <param name="RequestHash">The request hash.</param>
/// <param name="Generation">The generation.</param>
internal sealed record BusyOutcomeReservation(string RequestHash, long Generation);
