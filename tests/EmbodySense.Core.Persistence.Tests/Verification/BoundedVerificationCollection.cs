using Xunit;

namespace EmbodySense.Core.Persistence.Tests.Verification;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BoundedVerificationCollection
{
    public const string Name = "Bounded verification profiling";
}
