namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Reports that validated, durably reconciled provider output could not be projected to the caller.</summary>
public sealed class GovernedModelResponsePublicationException : InvalidOperationException
{
    /// <summary>Creates the bounded, value-free caller-publication failure.</summary>
    public GovernedModelResponsePublicationException()
        : base("Durably reconciled governed model output could not be published to the caller.")
    {
    }
}
