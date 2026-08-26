using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record NodeJson(
    string? Id,
    string? Kind,
    string? TypeId,
    int DescriptorVersion,
    string[]? AuthorityCeiling,
    IReadOnlyDictionary<string, string>? Parameters,
    PortJson[]? Ports,
    GovernedModelRoutingPolicy? ModelRoutingPolicy,
    CapabilityDataClass[]? AuthoredInputDataClasses,
    GovernedLoopRetryPolicy? RetryPolicy,
    HumanInputNodeConfigurationJson? HumanInputConfiguration);
