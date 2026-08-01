using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using System.Text.Json;

if (args is ["capability", var behavior])
{
    return await HostCapabilityAsync(behavior);
}

if (args is ["hold-control", var workspaceRoot, var kindText, var runId, var versionText, var operationId])
{
    return await HoldControlOperationAsync(workspaceRoot, kindText, runId, versionText, operationId);
}

if (args is [var cancellationWorkspaceRoot, var cancellationRunId])
{
    return await HostCancellationAsync(cancellationWorkspaceRoot, cancellationRunId);
}

return 2;

static async Task<int> HostCapabilityAsync(string behavior)
{
    var input = await Console.In.ReadLineAsync() ?? "null";
    switch (behavior)
    {
        case "echo":
            Console.Write(input);
            return 0;
        case "malformed":
            Console.Write("not-json");
            return 0;
        case "crash":
            Console.Error.Write("password=hunter2 C:\\private\\secret.txt");
            return 7;
        case "hang":
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        case "oversize":
            Console.Write(new string('x', 128 * 1024));
            return 0;
        case "environment":
            Console.Write(JsonSerializer.Serialize(Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(value => value.ToString()).OrderBy(value => value, StringComparer.Ordinal)));
            return 0;
        case "working-root":
            Console.Write(JsonSerializer.Serialize(Environment.CurrentDirectory));
            return 0;
        default:
            return 2;
    }
}

static async Task<int> HostCancellationAsync(string workspaceRoot, string runId)
{
    var paths = new WorkspacePaths(workspaceRoot);
    await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
    using var cancellation = new CancellationTokenSource();
    using var registration = gate.RegisterActiveAttempt(runId, cancellation);
    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    catch (OperationCanceledException exception)
    {
        _ = registration.TryConfirmProviderInterruption(exception.CancellationToken);
        Console.WriteLine("interrupted");
        await Console.Out.FlushAsync();
    }

    _ = Console.ReadLine();
    return 0;
}

static async Task<int> HoldControlOperationAsync(string workspaceRoot, string kindText, string runId, string versionText, string operationId)
{
    if (!Enum.TryParse<CustomLoopControlKind>(kindText, ignoreCase: true, out var kind) || kind == CustomLoopControlKind.Unknown || !int.TryParse(versionText, out var expectedVersion))
    {
        return 2;
    }

    var now = DateTimeOffset.UtcNow.ToUniversalTime();
    var actor = AuditSchema.Actors.Web;
    var pending = new CustomLoopControlOperation(
        CustomLoopControlOperation.CurrentSchemaVersion,
        operationId,
        CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor),
        kind,
        runId,
        expectedVersion,
        actor,
        now,
        now,
        CustomLoopControlOperationState.Pending,
        CustomLoopControlStatus.Unknown,
        null,
        null,
        false,
        "The child process is holding the pre-transition control-operation execution lease.");
    var result = await new CustomLoopControlOperationStore(new WorkspacePaths(workspaceRoot)).BeginAsync(pending);
    using var lease = result.Lease;
    if (result.Status != CustomLoopControlOperationStoreStatus.Created || lease is null)
    {
        return 3;
    }

    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    _ = Console.ReadLine();
    return 0;
}
