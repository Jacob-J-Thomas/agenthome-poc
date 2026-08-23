using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

internal sealed class ConfiguredModelProfileInferenceClientLease(
    GovernedModelProfilePin primary,
    ExactModelProfileEnforcementAcknowledgement enforcement,
    LlmInferenceClient client,
    ConfiguredModelExecutableSnapshotLease executable) : IExactModelProfileInferenceClientLease
{
    public string ProfilePinHash => primary.ContentHash;

    public string ConfigurationHash => primary.Metadata.ConfigurationHash;

    public ExactModelProfileEnforcementAcknowledgement Enforcement => enforcement;

    public ILlmInferenceClient Client => client;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await executable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
