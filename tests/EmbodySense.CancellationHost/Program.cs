using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

if (args.Length != 2)
{
    return 2;
}

var paths = new WorkspacePaths(args[0]);
var runId = args[1];
await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
using var cancellation = new CancellationTokenSource();
using var registration = gate.RegisterActiveAttempt(runId, cancellation);
Console.WriteLine("ready");
await Console.Out.FlushAsync();
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
}
catch (OperationCanceledException)
{
    registration.ConfirmProviderInterruption();
    Console.WriteLine("interrupted");
    await Console.Out.FlushAsync();
}

_ = Console.ReadLine();
return 0;
