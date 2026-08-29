using System.Diagnostics;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Publication;

internal sealed class HumanInputRequestPublicationProcessScenario : IAsyncDisposable
{
    private readonly HumanInputContinuationRecoveryContext _context;
    private readonly DateTimeOffset _now;
    private readonly TestWorkspace _workspace;

    private HumanInputRequestPublicationProcessScenario(
        TestWorkspace workspace,
        HumanInputContinuationRecoveryContext context,
        DateTimeOffset now)
    {
        _workspace = workspace;
        _context = context;
        _now = now;
    }

    internal static async Task<HumanInputRequestPublicationProcessScenario> CreateAsync()
    {
        var workspace = new TestWorkspace();
        var fixture = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext("human-input-publication-process-run");
        var now = HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(1).AddSeconds(30);
        var running = fixture.RunningRun with
        {
            Events =
            [
                .. fixture.RunningRun.Events,
                new CustomLoopRunEvent(3, "human-input-publication-running", fixture.RunningRun.UpdatedAtUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered running.", [], null, null, null, null, null, null, null, null, null, null),
            ],
        };
        var waiting = fixture.Run with
        {
            Events = [.. running.Events, fixture.Run.Events[^1] with { Sequence = 4 }],
        };
        var context = fixture with { RunningRun = running, Run = waiting };
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);

        // The process host has no public action that isolates the ordered runner's checkpoint-creation stage. Persisting
        // this validator-proved fixture through the production run store keeps the external-process coverage focused on
        // the production checkpoint-to-request publication boundary and its recovery behavior.
        using (var runs = new CustomLoopRunStore(paths, new HumanInputContinuationTestClock(now)))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await runs.CreateAsync(context.AdmittedRun)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.RunningRun, context.AdmittedRun.LifecycleVersion)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.Run, context.RunningRun.LifecycleVersion)).Status);
        }

        Assert.True(AuthorityGrantJson.TrySerialize(context.Grant, out var grantJson, out var validation), string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
        await File.WriteAllTextAsync(System.IO.Path.Combine(workspace.RootPath, "publication-grant.json"), grantJson!).ConfigureAwait(false);
        return new HumanInputRequestPublicationProcessScenario(workspace, context, now);
    }

    internal Process Start(string crashBoundary, string resultName)
        => HumanInputContinuationHostProcess.Start(
            "publication",
            _workspace.RootPath,
            _context.Run.Id,
            _context.Checkpoint.Binding.CheckpointId,
            _context.Checkpoint.CheckpointHash,
            _now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path("publication-grant.json"),
            crashBoundary,
            "1",
            Path(resultName + ".result"));

    internal async Task<string> RunAsync(string resultName)
    {
        using var process = Start("none", resultName);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        var resultPath = Path(resultName + ".result");
        var result = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath).ConfigureAwait(false) : "<result-not-written>";
        Assert.True(process.ExitCode == 0, $"Expected successful publication host exit. result: {result}; stderr: {standardError}");
        return result;
    }

    internal async Task<HumanInputRequestLifecycleStoreReadResult> ReadRequestAsync()
    {
        var store = new HumanInputRequestStore(new EmbodySense.Core.Common.Workspace.WorkspacePaths(_workspace.RootPath));
        return await store.ReadAsync(_context.Checkpoint.Request.RequestId).ConfigureAwait(false);
    }

    internal string Path(string name) => System.IO.Path.Combine(_workspace.RootPath, name);

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
