using System.Diagnostics;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Publication;

public sealed class HumanInputRequestPublicationProcessTests
{
    [Fact]
    public async Task Process_loss_after_canonical_checkpoint_durability_before_request_create_acknowledgement_recovers_one_publication()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();

        using var interrupted = scenario.Start("TrustInitialized", "checkpoint-durable-before-create");
        await AssertExitsAsync(interrupted, 86, scenario.Path("checkpoint-durable-before-create.result"));

        var recovered = await scenario.RunAsync("checkpoint-durable-recovery");
        var replayed = await scenario.RunAsync("checkpoint-durable-replay");

        Assert.Equal("Published", recovered);
        Assert.Equal("Replayed", replayed);
        await AssertExactlyOneCreateAsync(scenario);
    }

    [Fact]
    public async Task Process_loss_after_request_create_durability_before_caller_acknowledgement_replays_without_a_second_delivery_opportunity()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();

        using var interrupted = scenario.Start("TrustAdvanced", "create-durable-before-acknowledgement");
        await AssertExitsAsync(interrupted, 86, scenario.Path("create-durable-before-acknowledgement.result"));
        await AssertExactlyOneCreateAsync(scenario);

        var recovered = await scenario.RunAsync("create-durable-recovery");
        var replayed = await scenario.RunAsync("create-durable-replay");

        Assert.Equal("Replayed", recovered);
        Assert.Equal("Replayed", replayed);
        await AssertExactlyOneCreateAsync(scenario);
    }

    private static async Task AssertExactlyOneCreateAsync(HumanInputRequestPublicationProcessScenario scenario)
    {
        var read = await scenario.ReadRequestAsync();

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, read.Status);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(read.PrimarySnapshot);
        Assert.Single(snapshot.RequestVersions);
        var create = Assert.Single(snapshot.Operations);
        Assert.Equal(HumanInputRequestLifecycleOperationKind.Create, create.Kind);
        Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, create.Outcome);
        Assert.Equal(snapshot.RequestVersions[0].RequestId, create.TargetRequestId);
        var candidate = Assert.IsType<HumanInputRequestReference>(create.CandidateRequest);
        Assert.Equal(snapshot.RequestVersions[0].RequestHash, candidate.RequestHash);
    }

    private static async Task AssertExitsAsync(Process process, int expectedExitCode, string resultPath)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        var result = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath).ConfigureAwait(false) : "<result-not-written>";
        Assert.True(process.ExitCode == expectedExitCode, $"Expected exit code {expectedExitCode}, actual {process.ExitCode}. result: {result}; stderr: {standardError}");
    }
}
