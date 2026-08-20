using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class LoopRunControlInputTests
{
    [Fact]
    public void Constructor_accepts_exact_identifiers_and_a_positive_version()
    {
        var input = new LoopRunControlInput("run-one", 3, "pause-one");

        Assert.Equal("run-one", input.RunId);
        Assert.Equal(3, input.ExpectedLifecycleVersion);
        Assert.Equal("pause-one", input.OperationId);
    }

    [Theory]
    [InlineData("", 1, "pause-one")]
    [InlineData("Run-One", 1, "pause-one")]
    [InlineData("run-one", 1, "")]
    [InlineData("run-one", 1, "Pause-One")]
    public void Constructor_rejects_noncanonical_identifiers(string runId, int expectedLifecycleVersion, string operationId)
    {
        Assert.Throws<ArgumentException>(() => new LoopRunControlInput(runId, expectedLifecycleVersion, operationId));
    }

    [Fact]
    public void Constructor_rejects_nonpositive_lifecycle_versions_before_runtime_recovery()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopRunControlInput("run-one", 0, "pause-one"));
    }

    [Fact]
    public void With_expression_cannot_bypass_validation()
    {
        var input = new LoopRunControlInput("run-one", 1, "pause-one");

        Assert.Throws<ArgumentException>(() => input with { OperationId = "Pause-One" });
        Assert.Throws<ArgumentOutOfRangeException>(() => input with { ExpectedLifecycleVersion = 0 });
    }
}
