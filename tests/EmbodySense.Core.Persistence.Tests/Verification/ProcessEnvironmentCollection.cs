using Xunit;

namespace EmbodySense.Core.Persistence.Tests.Verification;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
