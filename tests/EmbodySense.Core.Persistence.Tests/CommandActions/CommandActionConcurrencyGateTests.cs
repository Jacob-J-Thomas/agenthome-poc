using EmbodySense.Core.Persistence.CommandActions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.CommandActions;

public sealed class CommandActionConcurrencyGateTests
{
    [Fact]
    public async Task Separate_gate_instances_share_the_same_workspace_slot_ceiling()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new CommandActionConcurrencyGate(paths);
        var second = new CommandActionConcurrencyGate(paths);
        var templateHash = new string('a', 64);
        await using var owned = Assert.IsAssignableFrom<IAsyncDisposable>(await first.TryAcquireAsync(templateHash, 1, TimeSpan.FromSeconds(1)));

        Assert.Null(await second.TryAcquireAsync(templateHash, 1, TimeSpan.FromMilliseconds(75)));

        await owned.DisposeAsync();
        await using var reacquired = Assert.IsAssignableFrom<IAsyncDisposable>(await second.TryAcquireAsync(templateHash, 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Registered_two_slot_ceiling_admits_exactly_two_concurrent_owners()
    {
        using var workspace = new TestWorkspace();
        var gate = new CommandActionConcurrencyGate(new WorkspacePaths(workspace.RootPath));
        var templateHash = new string('b', 64);
        await using var first = Assert.IsAssignableFrom<IAsyncDisposable>(await gate.TryAcquireAsync(templateHash, 2, TimeSpan.FromSeconds(1)));
        await using var second = Assert.IsAssignableFrom<IAsyncDisposable>(await gate.TryAcquireAsync(templateHash, 2, TimeSpan.FromSeconds(1)));

        Assert.Null(await gate.TryAcquireAsync(templateHash, 2, TimeSpan.FromMilliseconds(75)));
    }

    [Fact]
    public async Task External_process_owner_blocks_the_same_registered_workspace_slot_until_release()
    {
        using var workspace = new TestWorkspace();
        var readyMarker = Path.Combine(workspace.RootPath, "command-action.ready");
        var releaseMarker = Path.Combine(workspace.RootPath, "command-action.release");
        var templateHash = new string('c', 64);
        using var process = CancellationHostProcess.Start(
            "command-action-concurrency",
            workspace.RootPath,
            templateHash,
            "1",
            readyMarker,
            releaseMarker);

        try
        {
            await WaitForMarkerAsync(readyMarker, process, TimeSpan.FromSeconds(15));
            var gate = new CommandActionConcurrencyGate(new WorkspacePaths(workspace.RootPath));

            Assert.Null(await gate.TryAcquireAsync(templateHash, 1, TimeSpan.FromMilliseconds(100)));

            await File.WriteAllTextAsync(releaseMarker, "release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            await using var reacquired = Assert.IsAssignableFrom<IAsyncDisposable>(await gate.TryAcquireAsync(templateHash, 1, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task WaitForMarkerAsync(string path, System.Diagnostics.Process process, TimeSpan timeout)
    {
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The command concurrency host exited before publishing readiness (exit {process.ExitCode}).");
            }

            if (TimeProvider.System.GetElapsedTime(startedAt) >= timeout)
            {
                throw new TimeoutException("The command concurrency host did not publish readiness within the bounded allowance.");
            }

            await Task.Delay(10);
        }
    }
}
