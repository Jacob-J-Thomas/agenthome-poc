using System.Diagnostics;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

public sealed class GovernedLoopCoordinatorProcessTests
{
    private static readonly DateTimeOffset _acquiredAtUtc = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Separate_os_processes_preserve_a_live_generic_lease_then_take_over_at_its_exact_expiry_and_fence_the_stale_owner()
    {
        using var workspace = new TestWorkspace();

        Assert.Equal("Acquired", await RunCoordinatorAsync(workspace.RootPath, "initial", "process-owner-one", 1, _acquiredAtUtc, "initial"));
        Assert.Equal("LeaseNotExpired", await RunCoordinatorAsync(workspace.RootPath, "handoff", "process-owner-two", 2, _acquiredAtUtc.AddMinutes(1).AddTicks(-1), "live"));
        Assert.Equal("Acquired", await RunCoordinatorAsync(workspace.RootPath, "handoff", "process-owner-two", 2, _acquiredAtUtc.AddMinutes(1), "takeover"));
        Assert.Equal("OwnershipLost", await RunCoordinatorAsync(workspace.RootPath, "renew", "process-owner-one", 1, _acquiredAtUtc, "stale"));
    }

    [Fact]
    public async Task Two_external_generic_workers_racing_at_expiry_have_exactly_one_durable_handoff_winner()
    {
        using var workspace = new TestWorkspace();
        Assert.Equal("Acquired", await RunCoordinatorAsync(workspace.RootPath, "initial", "process-owner-one", 1, _acquiredAtUtc, "initial"));

        var first = RunCoordinatorAsync(workspace.RootPath, "handoff", "process-owner-two", 2, _acquiredAtUtc.AddMinutes(1), "first");
        var second = RunCoordinatorAsync(workspace.RootPath, "handoff", "process-owner-three", 2, _acquiredAtUtc.AddMinutes(1), "second");
        var results = await Task.WhenAll(first, second);

        Assert.True(
            results.Count(result => result == "Acquired") == 1
                && results.Count(result => result is "Conflict" or "LeaseNotExpired") == 1,
            $"Expected one acquired handoff and one losing worker that observed either the contested predecessor or the winner's live lease, actual: {string.Join(", ", results)}.");
    }

    private static async Task<string> RunCoordinatorAsync(
        string workspaceRoot,
        string mode,
        string ownerId,
        long epoch,
        DateTimeOffset acquiredAtUtc,
        string resultName)
    {
        var resultPath = Path.Combine(workspaceRoot, resultName + ".result");
        using var process = HumanInputContinuationHostProcess.Start(
            "coordinator",
            mode,
            workspaceRoot,
            ownerId,
            epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            acquiredAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            resultPath);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, $"The generic coordinator host failed: {standardError}");
        return await File.ReadAllTextAsync(resultPath);
    }
}
