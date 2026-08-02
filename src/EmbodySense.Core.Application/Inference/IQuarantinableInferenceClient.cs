namespace EmbodySense.Core.Application.Inference;

/// <summary>
/// Abandons provider transport state after an attempt whose external outcome cannot be proved.
/// </summary>
public interface IQuarantinableInferenceClient
{
    /// <summary>
    /// Disposes any live provider transport so later work cannot consume stale responses or requests.
    /// </summary>
    Task QuarantineAsync(CancellationToken cancellationToken = default);
}
