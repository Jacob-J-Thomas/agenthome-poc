using System.Diagnostics;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationProcessScenario : IAsyncDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly HumanInputContinuationRecoveryContext _context;
    private readonly DateTimeOffset _now;

    private HumanInputResponseContinuationProcessScenario(TestWorkspace workspace, HumanInputContinuationRecoveryContext context, DateTimeOffset now)
    {
        _workspace = workspace;
        _context = context;
        _now = now;
    }

    internal static async Task<HumanInputResponseContinuationProcessScenario> CreateAsync(HumanInputRequestLifecycleOperationKind? noResponseTerminalOperation = null)
    {
        var workspace = new TestWorkspace();
        var fixture = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var now = noResponseTerminalOperation == HumanInputRequestLifecycleOperationKind.Expire
            ? fixture.Checkpoint.Request.Timing.ExpiresAtUtc.AddTicks(1)
            : HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(1).AddSeconds(30);
        var running = fixture.RunningRun with
        {
            Events =
            [
                .. fixture.RunningRun.Events,
                new CustomLoopRunEvent(3, "human-input-continuation-running", fixture.RunningRun.UpdatedAtUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered running.", [], null, null, null, null, null, null, null, null, null, null),
            ],
        };
        var waiting = fixture.Run with
        {
            Events = [.. running.Events, fixture.Run.Events[^1] with { Sequence = 4 }],
        };
        var context = fixture with { RunningRun = running, Run = waiting };
        using var runs = new CustomLoopRunStore(new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath), new HumanInputContinuationTestClock(now));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await runs.CreateAsync(context.AdmittedRun)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.RunningRun, context.AdmittedRun.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.Run, context.RunningRun.LifecycleVersion)).Status);
        await SeedResponseOrNoResponseTerminalAsync(workspace.RootPath, context, now, noResponseTerminalOperation);
        return new HumanInputResponseContinuationProcessScenario(workspace, context, now);
    }

    internal string Path(string name) => System.IO.Path.Combine(_workspace.RootPath, name);

    internal Process Start(string crashPlane, string crashBoundary, int crashOrdinal, string resultName, string readyPath = "-", string releasePath = "-")
        => HumanInputContinuationHostProcess.Start(
            "wake",
            _workspace.RootPath,
            _context.Run.Id,
            _context.Checkpoint.Binding.CheckpointId,
            _now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            crashPlane,
            crashBoundary,
            crashOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            readyPath,
            releasePath,
            Path(resultName + ".result"));

    internal async Task<string> RunAsync(string resultName)
    {
        using var process = Start("none", "none", 1, resultName);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        var resultPath = Path(resultName + ".result");
        var result = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "<result-not-written>";
        var diagnosticPath = resultPath + ".diagnostic";
        var diagnostic = File.Exists(diagnosticPath) ? await File.ReadAllTextAsync(diagnosticPath) : "<diagnostic-not-written>";
        Assert.True(process.ExitCode == 0, $"Expected successful continuation host exit. result: {result}; diagnostic: {diagnostic}. stderr: {standardError}");
        return result;
    }

    internal async Task<CustomLoopRunRecord> ReadRunAsync()
    {
        using var runs = new CustomLoopRunStore(new EmbodySense.Core.Common.Workspace.WorkspacePaths(_workspace.RootPath), new HumanInputContinuationTestClock(_now));
        return await runs.GetAsync(_context.Run.Id) ?? throw new InvalidOperationException("The canonical run was not found.");
    }

    internal CustomLoopRunStore OpenRunStore()
        => new(new EmbodySense.Core.Common.Workspace.WorkspacePaths(_workspace.RootPath), new HumanInputContinuationTestClock(_now));

    internal async Task<string> ReadAuditEvidenceAsync()
    {
        var auditPath = new EmbodySense.Core.Common.Workspace.WorkspacePaths(_workspace.RootPath).AuditPath;
        if (!Directory.Exists(auditPath))
        {
            return string.Empty;
        }

        var evidence = new List<string>();
        foreach (var path in Directory.EnumerateFiles(auditPath, "*", SearchOption.AllDirectories))
        {
            evidence.Add(await File.ReadAllTextAsync(path));
        }

        return string.Join(Environment.NewLine, evidence);
    }

    internal void DamageResponseStore(string damage)
    {
        var path = new EmbodySense.Core.Common.Workspace.WorkspacePaths(_workspace.RootPath).AgentFile(System.IO.Path.Combine("human-input", "requests", "lifecycle.json"));
        Assert.True(File.Exists(path), "The seeded canonical Human Input request ledger was not found.");
        switch (damage)
        {
            case "deleted":
                File.Delete(path);
                break;
            case "corrupted":
                File.WriteAllText(path, "{");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task SeedResponseOrNoResponseTerminalAsync(
        string workspaceRoot,
        HumanInputContinuationRecoveryContext context,
        DateTimeOffset now,
        HumanInputRequestLifecycleOperationKind? noResponseTerminalOperation)
    {
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspaceRoot);
        var store = new HumanInputRequestStore(paths);
        var request = context.Checkpoint.Request;
        var head = HumanInputRequestStoreTestData.Head(request, 1, HumanInputRequestLifecycleStatus.Pending, 0, null, null, "human-input-continuation-create", request.Timing.RequestedAtUtc);
        var evidence = HumanInputRequestStoreTestData.Evidence(HumanInputRequestLifecycleOperationKind.Create, request.RequestId, "human-input-continuation-create", HumanInputRequestStoreTestData.HashA, request.Timing.RequestedAtUtc, null, head, request);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(new HumanInputRequestLifecycleStoreMutation(0, evidence, request, head, null))).Status);
        if (noResponseTerminalOperation is { } terminalOperation)
        {
            var terminal = HumanInputRequestStoreTestData.TransitionMutation(terminalOperation, request, head, 1, "human-input-continuation-" + terminalOperation.ToString().ToLowerInvariant(), HumanInputRequestStoreTestData.HashB);
            terminal = terminal with { Operation = terminal.Operation with { ExpectedBinding = request.Binding } };
            Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(terminal)).Status);
            return;
        }

        Assert.True(AuthorityActorId.TryParse("user-one", out var actor, out _));
        var command = HumanInputResponseLifecycleCommandHash.Apply(new HumanInputResponseLifecycleCommand(
            HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
            "human-input-continuation-submit",
            HumanInputResponseOperationKind.Submit,
            request.RequestId,
            head.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestStoreTestData.Reference(request),
            request.Binding,
            "human-input-continuation-response",
            new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, true, null, null),
            null,
            [],
            string.Empty));
        var result = await new HumanInputResponseLifecycleService(store, new HumanInputContinuationResponseActorAuthenticator(actor!), new HumanInputContinuationAuthorityTransaction(), request.Binding.WorkspaceId, new HumanInputContinuationTestClock(now)).MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, result.Status);
    }
}
