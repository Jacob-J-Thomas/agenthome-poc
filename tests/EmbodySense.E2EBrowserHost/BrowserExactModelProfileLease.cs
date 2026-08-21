using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.E2EBrowserHost;

internal sealed class BrowserExactModelProfileLease(
    GovernedModelProfilePin primary,
    ExactModelProfileEnforcementAcknowledgement acknowledgement) : IExactModelProfileInferenceClientLease
{
    public string ProfilePinHash => primary.ContentHash;

    public string ConfigurationHash => primary.Metadata.ConfigurationHash;

    public ExactModelProfileEnforcementAcknowledgement Enforcement => acknowledgement;

    public ILlmInferenceClient Client { get; } = new BrowserExactOutputBoundInferenceClient(primary.Metadata.ModelId);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
