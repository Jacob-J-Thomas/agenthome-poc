namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies one observable semantic boundary in a governed provider attempt.</summary>
public enum GovernedModelPrimaryExecutionBoundary
{
    /// <summary>No budget reservation has been attempted.</summary>
    BeforeReservation = 1,
    /// <summary>The exact reservation is durably authenticated and provider dispatch has not begun.</summary>
    ReservationRetained = 2,
    /// <summary>The governed transport commit completed after durable authority and dispatch evidence.</summary>
    ProviderTransportCommitted = 3,
    /// <summary>The exact provider response returned but usage has not yet been retained.</summary>
    ProviderResponseReceived = 4,
    /// <summary>Explicit provider usage is durable but reconciliation and frontier adoption are not complete.</summary>
    UsageRetained = 5
}
