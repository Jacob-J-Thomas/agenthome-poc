using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Owns one fresh provider client for one exact admitted profile configuration.</summary>
public interface IExactModelProfileInferenceClientLease : IAsyncDisposable
{
    /// <summary>Gets the exact profile pin hash resolved by Startup.</summary>
    string ProfilePinHash { get; }
    /// <summary>Gets the exact private configuration hash without revealing configuration.</summary>
    string ConfigurationHash { get; }
    /// <summary>Gets exact affirmative adapter enforcement acknowledgement.</summary>
    ExactModelProfileEnforcementAcknowledgement Enforcement { get; }
    /// <summary>Gets the fresh provider client.</summary>
    ILlmInferenceClient Client { get; }
}
